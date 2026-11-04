using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Domain;
using Orchestrator.Providers;

namespace Orchestrator.UnitTests;

public sealed class AzureAiSearchIndexTests
{
    [Fact]
    public async Task SendsTenantFilteredHybridSemanticQueryAndMapsGroundingMetadata()
    {
        var handler = new RecordingHandler("""
            {
              "value": [
                {
                  "@search.score": 0.03,
                  "@search.rerankerScore": 3.2,
                  "documentId": "doc-1",
                  "chunkId": "chunk-1",
                  "title": "Travel policy",
                  "sourceUri": "https://example.invalid/travel-policy",
                  "locator": "Page 2",
                  "content": "The synthetic daily limit is 85 units.",
                  "tenantId": "tenant-a",
                  "accessTags": ["employee"]
                },
                {
                  "@search.score": 1.0,
                  "@search.rerankerScore": 4.0,
                  "documentId": "cross-tenant",
                  "chunkId": "chunk-2",
                  "title": "Forbidden",
                  "sourceUri": "https://example.invalid/forbidden",
                  "content": "Must not be returned.",
                  "tenantId": "tenant-b",
                  "accessTags": ["employee"]
                },
                {
                  "@search.score": 0.02,
                  "@search.rerankerScore": 1.0,
                  "documentId": "weak-match",
                  "chunkId": "chunk-3",
                  "title": "Unrelated procedure",
                  "sourceUri": "https://example.invalid/unrelated",
                  "content": "Authorized but insufficiently relevant evidence.",
                  "tenantId": "tenant-a",
                  "accessTags": ["employee"]
                }
              ]
            }
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://search.example.invalid/") };
        var adapter = new AzureAiSearchIndex(client, Options.Create(new AzureAiSearchOptions
        {
            Endpoint = "https://search.example.invalid",
            IndexName = "enterprise-agent-chunks",
            ApiKey = "unit-test-key"
        }));

        var hits = await adapter.SearchAsync(new SearchRequest(
            TenantId.Create("tenant-a"),
            "international travel limit",
            new HashSet<string> { "employee" },
            5,
            0.2), CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("doc-1", hit.DocumentId);
        Assert.Equal(0.8, hit.Score, 5);
        Assert.DoesNotContain(hits, item => item.TenantId == TenantId.Create("tenant-b"));
        Assert.DoesNotContain(hits, item => item.DocumentId == "weak-match");
        Assert.Equal("unit-test-key", handler.Request!.Headers.GetValues("api-key").Single());
        Assert.Contains("api-version=2026-04-01", handler.Request.RequestUri!.Query, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("semantic", body.RootElement.GetProperty("queryType").GetString());
        Assert.Equal("text", body.RootElement.GetProperty("vectorQueries")[0].GetProperty("kind").GetString());
        Assert.Equal(50, body.RootElement.GetProperty("vectorQueries")[0].GetProperty("k").GetInt32());
        var filter = body.RootElement.GetProperty("filter").GetString();
        Assert.Contains("tenantId eq 'tenant-a'", filter, StringComparison.Ordinal);
        Assert.Contains("search.in", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessUsesQueryKeyCompatibleDocumentCountEndpoint()
    {
        var handler = new RecordingHandler("3");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://search.example.invalid/") };
        var adapter = new AzureAiSearchIndex(client, Options.Create(new AzureAiSearchOptions
        {
            Endpoint = "https://search.example.invalid",
            IndexName = "enterprise-agent-chunks",
            ApiKey = "query-key"
        }));

        await adapter.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Contains("indexes/enterprise-agent-chunks/docs/$count", handler.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("query-key", handler.Request.Headers.GetValues("api-key").Single());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RecordingHandler(string response) => _response = response;

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
