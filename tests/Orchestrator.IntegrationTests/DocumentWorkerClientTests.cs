using System.Net;
using System.Text;
using Orchestrator.Application;
using Orchestrator.Domain;
using Orchestrator.Infrastructure;

namespace Orchestrator.IntegrationTests;

public sealed class DocumentWorkerClientTests
{
    [Fact]
    public async Task SearchTranslatesSnakeCaseWorkerContract()
    {
        const string json = """
            [{
              "document_id":"document-1",
              "chunk_id":"chunk-1",
              "title":"Policy",
              "source_uri":"demo://policy",
              "locator":"section 1",
              "content":"Verified content.",
              "score":0.91,
              "tenant_id":"demo-tenant",
              "access_tags":["employee"]
            }]
            """;
        using var client = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("http://document-worker/")
        };
        var adapter = new DocumentWorkerClient(client);

        var hits = await adapter.SearchAsync(new SearchRequest(
            TenantId.Create("demo-tenant"), "policy", new HashSet<string> { "employee" },
            5, 0.2), CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("document-1", hit.DocumentId);
        Assert.Equal("chunk-1", hit.ChunkId);
        Assert.Equal(TenantId.Create("demo-tenant"), hit.TenantId);
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/search", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
