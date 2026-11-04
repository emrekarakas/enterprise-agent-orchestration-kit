using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class AzureAiSearchOptions
{
    public const string SectionName = "AzureAiSearch";

    public string Endpoint { get; set; } = string.Empty;
    public string IndexName { get; set; } = "idx-eaokit-chunks-v1";
    public string ApiVersion { get; set; } = "2026-04-01";
    public string SemanticConfiguration { get; set; } = "sem-eaokit-grounding-v1";
    public string VectorField { get; set; } = "contentVector";
    public string CredentialMode { get; set; } = "DefaultAzureCredential";
    public string? ManagedIdentityClientId { get; set; }
    public string? ApiKey { get; set; }
}

public static class AzureAiSearchCredentialFactory
{
    public static TokenCredential Create(AzureAiSearchOptions options)
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
}

public sealed class AzureAiSearchIndex : ISearchIndex
{
    private static readonly ActivitySource ActivitySource = new("Enterprise.AgentKit");
    private static readonly TokenRequestContext SearchTokenContext = new(["https://search.azure.com/.default"]);
    private readonly HttpClient _httpClient;
    private readonly AzureAiSearchOptions _options;
    private readonly TokenCredential? _credential;

    public AzureAiSearchIndex(
        HttpClient httpClient,
        IOptions<AzureAiSearchOptions> options,
        TokenCredential? credential = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credential = credential;
        ValidateOptions(_options, credential);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("retrieval.azure_ai_search", ActivityKind.Client);
        activity?.SetTag("db.system", "azure_ai_search");
        activity?.SetTag("search.mode", "hybrid_semantic");
        activity?.SetTag("search.index", _options.IndexName);

        var endpoint = $"indexes/{Uri.EscapeDataString(_options.IndexName)}/docs/search?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        await AuthenticateAsync(message, cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(new
        {
            search = request.Query,
            searchFields = "title,content",
            queryType = "semantic",
            semanticConfiguration = _options.SemanticConfiguration,
            semanticErrorHandling = "partial",
            select = "documentId,chunkId,title,sourceUri,locator,content,tenantId,accessTags",
            filter = BuildSecurityFilter(request),
            top = request.Top,
            vectorFilterMode = "preFilter",
            vectorQueries = new[]
            {
                new
                {
                    kind = "text",
                    text = request.Query,
                    fields = _options.VectorField,
                    k = 50
                }
            }
        });

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                activity?.SetTag("error.type", "azure_ai_search_http");
                throw new DomainException(
                    ErrorCategory.ExternalDependency,
                    "search.azure_failed",
                    $"Azure AI Search returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseHits(document.RootElement, request);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new DomainException(
                exception is TaskCanceledException ? ErrorCategory.Timeout : ErrorCategory.ExternalDependency,
                exception is TaskCanceledException ? "search.azure_timeout" : "search.azure_unavailable",
                "Azure AI Search is unavailable.",
                exception);
        }
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        var endpoint = $"indexes/{Uri.EscapeDataString(_options.IndexName)}?api-version={Uri.EscapeDataString(_options.ApiVersion)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        await AuthenticateAsync(message, cancellationToken).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DomainException(
                ErrorCategory.ExternalDependency,
                "search.azure_not_ready",
                $"Azure AI Search readiness probe returned HTTP {(int)response.StatusCode}.");
        }
    }

    private async Task AuthenticateAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Add("api-key", _options.ApiKey);
            return;
        }

        var token = await _credential!.GetTokenAsync(SearchTokenContext, cancellationToken).ConfigureAwait(false);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static SearchHit[] ParseHits(JsonElement root, SearchRequest request)
    {
        if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new DomainException(ErrorCategory.ExternalDependency, "search.azure_invalid_response", "Azure AI Search returned an invalid response.");
        }

        var hits = new List<SearchHit>();
        foreach (var item in values.EnumerateArray())
        {
            var tenant = RequiredString(item, "tenantId");
            if (!string.Equals(tenant, request.TenantId.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var accessTags = item.TryGetProperty("accessTags", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Select(tag => tag.GetString()).Where(tag => tag is not null).Select(tag => tag!).ToArray()
                : [];
            if (accessTags.Length > 0 && !accessTags.Any(request.AccessTags.Contains))
            {
                continue;
            }

            var rawScore = item.TryGetProperty("@search.rerankerScore", out var reranker)
                && reranker.ValueKind == JsonValueKind.Number
                ? Math.Clamp(reranker.GetDouble() / 4d, 0d, 1d)
                : item.GetProperty("@search.score").GetDouble();
            if (rawScore < request.MinimumScore)
            {
                continue;
            }

            hits.Add(new SearchHit(
                RequiredString(item, "documentId"),
                RequiredString(item, "chunkId"),
                RequiredString(item, "title"),
                RequiredString(item, "sourceUri"),
                OptionalString(item, "locator"),
                RequiredString(item, "content"),
                rawScore,
                TenantId.Create(tenant),
                accessTags));
        }

        return hits.OrderByDescending(hit => hit.Score).Take(request.Top).ToArray();
    }

    private static string BuildSecurityFilter(SearchRequest request)
    {
        var tenant = EscapeOData(request.TenantId.Value);
        if (request.AccessTags.Count == 0)
        {
            return $"tenantId eq '{tenant}' and not accessTags/any()";
        }

        var tags = string.Join(',', request.AccessTags.Order(StringComparer.Ordinal).Select(EscapeSearchIn));
        return $"tenantId eq '{tenant}' and (not accessTags/any() or accessTags/any(tag: search.in(tag, '{tags}', ',')))";
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new DomainException(ErrorCategory.ExternalDependency, "search.azure_invalid_response", $"Azure AI Search omitted {name}.");

    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string EscapeOData(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string EscapeSearchIn(string value) => EscapeOData(value.Replace(",", string.Empty, StringComparison.Ordinal));

    private static void ValidateOptions(AzureAiSearchOptions options, TokenCredential? credential)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("AzureAiSearch:Endpoint must be an absolute HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(options.IndexName)
            || string.IsNullOrWhiteSpace(options.SemanticConfiguration)
            || string.IsNullOrWhiteSpace(options.VectorField))
        {
            throw new InvalidOperationException("Azure AI Search index, semantic configuration, and vector field are required.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey) && credential is null)
        {
            throw new InvalidOperationException("Azure AI Search requires a TokenCredential or API key.");
        }
    }
}
