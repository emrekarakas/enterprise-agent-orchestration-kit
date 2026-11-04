using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Providers;

namespace Orchestrator.UnitTests;

public sealed class OpenAiCompatibleProviderTests
{
    private static readonly float[] ExpectedEmbedding = [0.1f, 0.2f, 0.3f];

    [Fact]
    public async Task AdapterParsesChatStructuredOutputAndEmbeddingsWithoutProviderTypesLeaking()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(new
            {
                choices = new[] { new { message = new { content = "Grounded response [1]" } } },
                usage = new { prompt_tokens = 10, completion_tokens = 4 }
            }),
            Json(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "{\"agentNames\":[\"knowledge\"],\"runInParallel\":false,\"maxSteps\":2,\"reasonCode\":\"knowledge\"}"
                        }
                    }
                }
            }),
            Json(new
            {
                data = new List<object> { new { embedding = ExpectedEmbedding } },
                usage = new { prompt_tokens = 3 }
            })
        ]);
        using var httpClient = new HttpClient(new QueueHandler(responses))
        {
            BaseAddress = new Uri("https://compatible.example/v1/")
        };
        var provider = new OpenAiCompatibleProvider(httpClient, Options.Create(new OpenAiCompatibleOptions
        {
            Endpoint = httpClient.BaseAddress.ToString(),
            ChatModel = "local-chat",
            StructuredOutputModel = "local-structured",
            EmbeddingModel = "local-embedding",
            ApiKey = "test-only"
        }));

        var chat = await provider.CompleteAsync(new ChatCompletionRequest(
            "answer", "Use evidence.", "Question", [], 100, 0), CancellationToken.None);
        var fallback = new RouteDecision(["knowledge"], false, 1, "fallback");
        var structured = await provider.CompleteAsync(new StructuredCompletionRequest<RouteDecision>(
            "route", "Question", "{\"type\":\"object\"}", fallback), CancellationToken.None);
        var embedding = await provider.EmbedAsync("Question", CancellationToken.None);

        Assert.Equal("Grounded response [1]", chat.Text);
        Assert.Equal(10, chat.InputTokens);
        Assert.False(structured.UsedFallback);
        Assert.Equal("knowledge", structured.Value.AgentNames.Single());
        Assert.Equal(ExpectedEmbedding, embedding.Vector);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task InvalidStructuredJsonUsesTypedDeterministicFallback()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(new
            {
                choices = new[] { new { message = new { content = "not-json" } } }
            })
        ]);
        using var httpClient = new HttpClient(new QueueHandler(responses))
        {
            BaseAddress = new Uri("https://compatible.example/v1/")
        };
        var provider = new OpenAiCompatibleProvider(httpClient, Options.Create(new OpenAiCompatibleOptions
        {
            Endpoint = httpClient.BaseAddress.ToString(),
            ChatModel = "local-chat",
            StructuredOutputModel = "local-structured",
            EmbeddingModel = "local-embedding",
            ApiKey = "test-only"
        }));
        var fallback = new RouteDecision(["knowledge"], false, 1, "invalid_json_fallback");

        var result = await provider.CompleteAsync(new StructuredCompletionRequest<RouteDecision>(
            "route", "Question", "{\"type\":\"object\"}", fallback), CancellationToken.None);

        Assert.True(result.UsedFallback);
        Assert.Same(fallback, result.Value);
        Assert.Equal("local-structured", result.Model);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value)
    };

    private sealed class QueueHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(responses.Dequeue());
        }
    }
}
