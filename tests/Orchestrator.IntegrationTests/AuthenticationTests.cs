using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orchestrator.Contracts;

namespace Orchestrator.IntegrationTests;

public sealed class AuthenticationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthenticationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task DevelopmentTokenAuthenticatesAndCarriesTenantContext()
    {
        using var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Persistence:Provider", "InMemory");
            builder.UseSetting("Authentication:Mode", "DevelopmentTokens");
        }).CreateClient();

        var anonymous = await client.GetAsync("/api/v1/conversations");
        Assert.True(
            anonymous.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected 401 but received {(int)anonymous.StatusCode}: {await anonymous.Content.ReadAsStringAsync()}");

        var tokenResponse = await client.PostAsJsonAsync("/api/v1/auth/development-token", new DevelopmentTokenRequest(
            "tenant-auth-test",
            "user-auth-test",
            ["knowledge.read"],
            ["employee"]));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<DevelopmentTokenResponse>();
        Assert.NotNull(token);
        Assert.Equal("Bearer", token.TokenType);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var created = await client.PostAsJsonAsync("/api/v1/conversations", new CreateConversationRequest("JWT scoped"));
        created.EnsureSuccessStatusCode();
        var conversation = await created.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        var listed = await client.GetFromJsonAsync<ConversationResponse[]>("/api/v1/conversations");
        Assert.Contains(listed ?? [], item => item.Id == conversation.Id);
    }
}
