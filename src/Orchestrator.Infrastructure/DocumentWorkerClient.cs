using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Infrastructure;

public sealed class DocumentWorkerOptions
{
    public const string SectionName = "DocumentWorker";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:8090";
    public int TimeoutSeconds { get; set; } = 30;
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
}

public sealed class DocumentWorkerClient : IDocumentIngestionGateway, ISearchIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly HttpClient _client;

    public DocumentWorkerClient(HttpClient client) => _client = client;

    public async Task<DocumentIngestion> UploadAsync(
        DocumentUpload upload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/ingestions");
        request.Headers.Add("X-Tenant-Id", upload.TenantId.Value);
        request.Headers.Add("X-User-Id", upload.UserId.Value);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(upload.Content);
        fileContent.Headers.ContentType = new(upload.ContentType);
        content.Add(fileContent, "file", upload.FileName);
        content.Add(new StringContent(string.Join(',', upload.AccessTags)), "access_tags");
        if (!string.IsNullOrWhiteSpace(upload.SourceUri))
        {
            content.Add(new StringContent(upload.SourceUri), "source_uri");
        }

        request.Content = content;
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentIngestion?> GetAsync(
        TenantId tenantId,
        string ingestionId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/ingestions/{Uri.EscapeDataString(ingestionId)}");
        request.Headers.Add("X-Tenant-Id", tenantId.Value);
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        TenantId tenantId,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"v1/documents/{Uri.EscapeDataString(documentId)}");
        request.Headers.Add("X-Tenant-Id", tenantId.Value);
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var body = new WorkerSearchRequest(
            request.TenantId.Value,
            request.Query,
            request.AccessTags.ToArray(),
            request.Top,
            request.MinimumScore);
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "v1/search")
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            },
            cancellationToken).ConfigureAwait(false);
        var hits = await response.Content.ReadFromJsonAsync<WorkerSearchHit[]>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];
        return hits.Select(hit => new SearchHit(
            hit.DocumentId,
            hit.ChunkId,
            hit.Title,
            hit.SourceUri,
            hit.Locator,
            hit.Content,
            hit.Score,
            TenantId.Create(hit.TenantId),
            hit.AccessTags)).ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        try
        {
            var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                return response;
            }

            response.Dispose();
            throw new DomainException(
                ErrorCategory.ExternalDependency,
                "document_worker.rejected",
                "The document service could not process the request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException(
                ErrorCategory.Timeout,
                "document_worker.timeout",
                "The document service timed out.");
        }
        catch (HttpRequestException error)
        {
            throw new DomainException(
                ErrorCategory.ProviderUnavailable,
                "document_worker.unavailable",
                "The document service is unavailable.",
                error);
        }
    }

    private static async Task<DocumentIngestion> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var item = await response.Content.ReadFromJsonAsync<WorkerIngestion>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DomainException(
                ErrorCategory.ExternalDependency,
                "document_worker.invalid_response",
                "The document service returned an invalid response.");
        return new DocumentIngestion(
            item.IngestionId, item.DocumentId, item.Status, item.FileName, item.ContentHash,
            item.ChunkCount, item.ErrorCode, item.ErrorMessage, item.CreatedAt, item.CompletedAt);
    }

    private sealed record WorkerSearchRequest(
        string TenantId,
        string Query,
        IReadOnlyList<string> AccessTags,
        int Top,
        double MinimumScore);

    private sealed record WorkerSearchHit(
        string DocumentId,
        string ChunkId,
        string Title,
        string SourceUri,
        string? Locator,
        string Content,
        double Score,
        string TenantId,
        IReadOnlyList<string> AccessTags);

    private sealed record WorkerIngestion(
        string IngestionId,
        string DocumentId,
        string Status,
        string FileName,
        string ContentHash,
        int ChunkCount,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);
}

public sealed class DisabledDocumentIngestionGateway : IDocumentIngestionGateway
{
    public Task<DocumentIngestion> UploadAsync(DocumentUpload upload, CancellationToken cancellationToken) =>
        throw Disabled();

    public Task<DocumentIngestion?> GetAsync(
        TenantId tenantId, string ingestionId, CancellationToken cancellationToken) =>
        throw Disabled();

    public Task<bool> DeleteAsync(
        TenantId tenantId, string documentId, CancellationToken cancellationToken) =>
        throw Disabled();

    private static DomainException Disabled() => new(
        ErrorCategory.ProviderUnavailable,
        "document_worker.disabled",
        "Document ingestion is disabled. Start the local stack or enable DocumentWorker configuration.");
}
