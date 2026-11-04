using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class OpenAiCompatibleOptions
{
    public const string SectionName = "OpenAiCompatible";

    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string ChatModel { get; set; } = string.Empty;
    public string StructuredOutputModel { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class OpenAiCompatibleProvider :
    IChatCompletionProvider,
    IStructuredCompletionProvider,
    IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleOptions _options;

    public OpenAiCompatibleProvider(HttpClient httpClient, IOptions<OpenAiCompatibleOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        Validate(_options);
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = request.SystemInstruction } };
        messages.AddRange(request.History.Select(message => (object)new
        {
            role = message.Role.ToString().ToLowerInvariant(),
            content = message.Content
        }));
        messages.Add(new { role = "user", content = request.Input });
        using var response = await PostAsync("chat/completions", new
        {
            model = _options.ChatModel,
            messages,
            temperature = request.Temperature,
            max_tokens = request.MaxOutputTokens
        }, cancellationToken).ConfigureAwait(false);
        var root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new ChatCompletionResult(
            ReadContent(root),
            ReadUsage(root, "prompt_tokens"),
            ReadUsage(root, "completion_tokens"),
            _options.ChatModel);
    }

    public async Task<StructuredCompletionResult<T>> CompleteAsync<T>(
        StructuredCompletionRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        var model = string.IsNullOrWhiteSpace(_options.StructuredOutputModel)
            ? _options.ChatModel
            : _options.StructuredOutputModel;
        try
        {
            using var schema = JsonDocument.Parse(request.JsonSchema);
            using var response = await PostAsync("chat/completions", new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = "Return only JSON that conforms exactly to the supplied schema." },
                    new { role = "user", content = request.Input }
                },
                temperature = 0,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = NormalizeSchemaName(request.Operation),
                        strict = true,
                        schema = schema.RootElement.Clone()
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            var root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var raw = ReadContent(root);
            var value = JsonSerializer.Deserialize<T>(raw, JsonOptions);
            return value is null
                ? new StructuredCompletionResult<T>(request.DeterministicFallback, true, model)
                : new StructuredCompletionResult<T>(value, false, model);
        }
        catch (JsonException)
        {
            return new StructuredCompletionResult<T>(request.DeterministicFallback, true, model);
        }
    }

    public async Task<EmbeddingResult> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("embeddings", new
        {
            model = _options.EmbeddingModel,
            input
        }, cancellationToken).ConfigureAwait(false);
        var root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var vector = root.GetProperty("data")[0].GetProperty("embedding")
            .EnumerateArray().Select(value => value.GetSingle()).ToArray();
        return new EmbeddingResult(vector, ReadUsage(root, "prompt_tokens"), _options.EmbeddingModel);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException(
                ErrorCategory.Timeout,
                "ai.openai_compatible_timeout",
                "The OpenAI-compatible provider timed out.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DomainException(
                ErrorCategory.ExternalDependency,
                "ai.openai_compatible_unavailable",
                "The OpenAI-compatible provider is unavailable.",
                exception);
        }
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new DomainException(
                ErrorCategory.ExternalDependency,
                "ai.openai_compatible_error",
                $"The OpenAI-compatible provider returned HTTP {(int)response.StatusCode}.");
        }

        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static string ReadContent(JsonElement root) =>
        root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
        ?? throw new JsonException("The provider response omitted message content.");

    private static int ReadUsage(JsonElement root, string propertyName) =>
        root.TryGetProperty("usage", out var usage)
        && usage.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var count)
            ? count
            : 0;

    private static string NormalizeSchemaName(string value)
    {
        var normalized = new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "structured_output" : normalized[..Math.Min(64, normalized.Length)];
    }

    private static void Validate(OpenAiCompatibleOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(options.ChatModel)
            || string.IsNullOrWhiteSpace(options.EmbeddingModel))
        {
            throw new InvalidOperationException(
                "OpenAiCompatible endpoint, chat model, and embedding model are required.");
        }
    }
}
