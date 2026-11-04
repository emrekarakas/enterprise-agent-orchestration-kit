using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = string.Empty;
    public string? StructuredOutputDeployment { get; set; }
    public string EmbeddingDeployment { get; set; } = string.Empty;
    public string CredentialMode { get; set; } = "DefaultAzureCredential";
    public string? ManagedIdentityClientId { get; set; }
    public string? ApiKey { get; set; }
    public bool SendMaxOutputTokens { get; set; } = true;
    public bool SendTemperature { get; set; } = true;
}

public static class AzureOpenAiClientFactory
{
    public static AzureOpenAIClient Create(AzureOpenAiOptions options)
    {
        Validate(options);
        var endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.ApiKey));
        }

        return new AzureOpenAIClient(endpoint, CreateCredential(options));
    }

    private static TokenCredential CreateCredential(AzureOpenAiOptions options)
    {
        var identity = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId);
        return string.Equals(options.CredentialMode, "ManagedIdentity", StringComparison.OrdinalIgnoreCase)
            ? new ManagedIdentityCredential(identity)
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = true,
                ManagedIdentityClientId = options.ManagedIdentityClientId
            });
    }

    private static void Validate(AzureOpenAiOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("AzureOpenAI:Endpoint must be an absolute HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(options.ChatDeployment)
            || string.IsNullOrWhiteSpace(options.EmbeddingDeployment))
        {
            throw new InvalidOperationException("Azure OpenAI chat and embedding deployment names are required.");
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        if (!string.Equals(options.CredentialMode, "DefaultAzureCredential", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.CredentialMode, "ManagedIdentity", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AzureOpenAI:CredentialMode must be DefaultAzureCredential or ManagedIdentity when ApiKey is empty.");
        }
    }
}

public sealed class AzureOpenAiProvider : IChatCompletionProvider, IStructuredCompletionProvider, IEmbeddingProvider
{
    private static readonly ActivitySource ActivitySource = new("Enterprise.AgentKit");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ChatClient _chatClient;
    private readonly ChatClient _structuredClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly AzureOpenAiOptions _options;

    static AzureOpenAiProvider()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public AzureOpenAiProvider(AzureOpenAIClient client, IOptions<AzureOpenAiOptions> options)
    {
        _options = options.Value;
        _chatClient = client.GetChatClient(_options.ChatDeployment);
        _structuredClient = client.GetChatClient(
            string.IsNullOrWhiteSpace(_options.StructuredOutputDeployment)
                ? _options.ChatDeployment
                : _options.StructuredOutputDeployment);
        _embeddingClient = client.GetEmbeddingClient(_options.EmbeddingDeployment);
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity("gen_ai.azure_openai.chat", request.Operation, _options.ChatDeployment);
        try
        {
            var messages = BuildMessages(request);
            var options = new ChatCompletionOptions();
            if (_options.SendMaxOutputTokens)
            {
                options.MaxOutputTokenCount = request.MaxOutputTokens;
            }

            if (_options.SendTemperature)
            {
                options.Temperature = (float)request.Temperature;
            }
            ChatCompletion completion = await _chatClient
                .CompleteChatAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            var text = string.Concat(completion.Content.Select(part => part.Text));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new DomainException(
                    ErrorCategory.InvalidModelOutput,
                    "ai.azure_openai.empty_response",
                    "Azure OpenAI returned an empty response.");
            }

            RecordUsage(activity, completion.Usage.InputTokenCount, completion.Usage.OutputTokenCount);
            return new ChatCompletionResult(
                text,
                completion.Usage.InputTokenCount,
                completion.Usage.OutputTokenCount,
                _options.ChatDeployment);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DependencyFailure("ai.azure_openai.timeout", "Azure OpenAI timed out.");
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException)
        {
            throw DependencyFailure("ai.azure_openai.unavailable", "Azure OpenAI is unavailable.", exception);
        }
    }

    public async Task<StructuredCompletionResult<T>> CompleteAsync<T>(
        StructuredCompletionRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        var deployment = string.IsNullOrWhiteSpace(_options.StructuredOutputDeployment)
            ? _options.ChatDeployment
            : _options.StructuredOutputDeployment;
        using var activity = StartActivity("gen_ai.azure_openai.structured", request.Operation, deployment);
        try
        {
            var messages = new ChatMessage[]
            {
                new SystemChatMessage(
                    "Return only JSON that conforms to the supplied schema. Do not add markdown or commentary."),
                new UserChatMessage(request.Input)
            };
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    SchemaName(request.Operation),
                    BinaryData.FromString(request.JsonSchema),
                    jsonSchemaIsStrict: true)
            };
            if (_options.SendTemperature)
            {
                options.Temperature = 0;
            }
            ChatCompletion completion = await _structuredClient
                .CompleteChatAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            var json = string.Concat(completion.Content.Select(part => part.Text));
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                activity?.SetTag("gen_ai.response.fallback", true);
                return new StructuredCompletionResult<T>(request.DeterministicFallback, true, deployment);
            }

            RecordUsage(activity, completion.Usage.InputTokenCount, completion.Usage.OutputTokenCount);
            return new StructuredCompletionResult<T>(value, false, deployment);
        }
        catch (JsonException)
        {
            activity?.SetTag("error.type", "invalid_model_json");
            activity?.SetTag("gen_ai.response.fallback", true);
            return new StructuredCompletionResult<T>(request.DeterministicFallback, true, deployment);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DependencyFailure("ai.azure_openai.timeout", "Azure OpenAI timed out.");
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException)
        {
            throw DependencyFailure("ai.azure_openai.unavailable", "Azure OpenAI is unavailable.", exception);
        }
    }

    public async Task<EmbeddingResult> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        using var activity = StartActivity("gen_ai.azure_openai.embedding", "embedding.generate", _options.EmbeddingDeployment);
        try
        {
            OpenAIEmbeddingCollection embeddings = await _embeddingClient
                .GenerateEmbeddingsAsync([input], null, cancellationToken)
                .ConfigureAwait(false);
            var embedding = embeddings[0];
            var vector = embedding.ToFloats().ToArray();
            if (vector.Length == 0)
            {
                throw new DomainException(
                    ErrorCategory.InvalidModelOutput,
                    "ai.azure_openai.empty_embedding",
                    "Azure OpenAI returned an empty embedding.");
            }

            activity?.SetTag("gen_ai.usage.input_tokens", embeddings.Usage.InputTokenCount);
            activity?.SetTag("gen_ai.embedding.dimensions", vector.Length);
            return new EmbeddingResult(vector, embeddings.Usage.InputTokenCount, _options.EmbeddingDeployment);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw DependencyFailure("ai.azure_openai.timeout", "Azure OpenAI timed out.");
        }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException)
        {
            throw DependencyFailure("ai.azure_openai.unavailable", "Azure OpenAI is unavailable.", exception);
        }
    }

    private static List<ChatMessage> BuildMessages(ChatCompletionRequest request)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(request.SystemInstruction) };
        foreach (var message in request.History)
        {
            messages.Add(message.Role switch
            {
                MessageRole.Assistant => new AssistantChatMessage(message.Content),
                MessageRole.System => new SystemChatMessage(message.Content),
                _ => new UserChatMessage(message.Content)
            });
        }

        messages.Add(new UserChatMessage(request.Input));
        return messages;
    }

    private static string SchemaName(string operation)
    {
        var characters = operation
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .Take(64)
            .ToArray();
        return characters.Length == 0 ? "structured_response" : new string(characters);
    }

    private static Activity? StartActivity(string name, string operation, string deployment)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "azure_openai");
        activity?.SetTag("gen_ai.operation.name", operation);
        activity?.SetTag("gen_ai.request.model", deployment);
        return activity;
    }

    private static void RecordUsage(Activity? activity, int inputTokens, int outputTokens)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
    }

    private static DomainException DependencyFailure(string code, string message, Exception? innerException = null) =>
        new(ErrorCategory.ExternalDependency, code, message, innerException);
}
