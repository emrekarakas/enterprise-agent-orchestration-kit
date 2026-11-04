using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orchestrator.Contracts;

namespace Orchestrator.EndToEndTests;

public sealed class CatalogSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public CatalogSmokeTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Persistence:Provider", "InMemory");
            builder.UseSetting("Authentication:Mode", "DevelopmentHeaders");
        }).CreateClient();

    [Fact]
    public async Task CatalogsExposeRegisteredAgentAndToolMetadata()
    {
        var agents = await _client.GetFromJsonAsync<AgentCatalogItemResponse[]>("/api/v1/catalog/agents");
        var tools = await _client.GetFromJsonAsync<ToolCatalogItemResponse[]>("/api/v1/catalog/tools");

        Assert.Contains(agents ?? [], item => item.Name == "knowledge");
        Assert.Contains(tools ?? [], item => item.Name == "enterprise-search" && item.MutationBehavior == "ReadOnly");
    }
}
