using Orchestrator.Providers;

namespace Orchestrator.UnitTests;

public sealed class AzureOpenAiOptionsTests
{
    [Fact]
    public void CreatesSdkClientWhenExplicitApiKeyConfigurationIsValid()
    {
        var options = ValidOptions();
        options.ApiKey = "unit-test-placeholder";

        var client = AzureOpenAiClientFactory.Create(options);

        Assert.NotNull(client);
    }

    [Fact]
    public void RejectsNonHttpsEndpointBeforeCreatingSdkClient()
    {
        var options = ValidOptions();
        options.Endpoint = "http://openai.example.invalid";

        var error = Assert.Throws<InvalidOperationException>(() => AzureOpenAiClientFactory.Create(options));

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingDeploymentNames()
    {
        var options = ValidOptions();
        options.ChatDeployment = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(() => AzureOpenAiClientFactory.Create(options));

        Assert.Contains("deployment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AzureOpenAiOptions ValidOptions() => new()
    {
        Endpoint = "https://openai.example.invalid",
        ChatDeployment = "chat-deployment",
        EmbeddingDeployment = "embedding-deployment",
        CredentialMode = "DefaultAzureCredential"
    };
}
