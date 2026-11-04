using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orchestrator.Contracts;

namespace Orchestrator.IntegrationTests;

public sealed class ApiVerticalSliceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiVerticalSliceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Persistence:Provider", "InMemory");
            builder.UseSetting("Authentication:Mode", "DevelopmentHeaders");
        }).CreateClient();
    }

    [Fact]
    public async Task MessageFlowReturnsGroundedAnswerAndReplaysIdempotentRequest()
    {
        var conversationResponse = await _client.PostAsJsonAsync("/api/v1/conversations", new CreateConversationRequest("Integration"));
        conversationResponse.EnsureSuccessStatusCode();
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("What is the international travel daily expense limit?"))
        };
        firstRequest.Headers.Add("Idempotency-Key", "integration-key-1");
        var firstResponse = await _client.SendAsync(firstRequest);
        Assert.True(firstResponse.IsSuccessStatusCode, await firstResponse.Content.ReadAsStringAsync());
        var first = await firstResponse.Content.ReadFromJsonAsync<AgentRunResponse>();

        Assert.NotNull(first);
        Assert.Equal("Completed", first.Status);
        Assert.Equal("knowledge", first.Agent);
        Assert.True(first.IsGrounded, JsonSerializer.Serialize(first));
        Assert.NotEmpty(first.Citations);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("What is the international travel daily expense limit?"))
        };
        replayRequest.Headers.Add("Idempotency-Key", "integration-key-1");
        var replayResponse = await _client.SendAsync(replayRequest);
        replayResponse.EnsureSuccessStatusCode();
        var replay = await replayResponse.Content.ReadFromJsonAsync<AgentRunResponse>();

        Assert.NotNull(replay);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.True(replay.Replayed);
    }

    [Fact]
    public async Task DocumentComparisonRunsMultipleSpecialistsAndCombinesTheirEvidence()
    {
        var conversationResponse = await _client.PostAsJsonAsync(
            "/api/v1/conversations",
            new CreateConversationRequest("Multi-agent comparison"));
        conversationResponse.EnsureSuccessStatusCode();
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest(
                "Compare the travel policy and information security policy."))
        };
        request.Headers.Add("Idempotency-Key", "multi-agent-comparison-1");

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var run = await response.Content.ReadFromJsonAsync<AgentRunResponse>();

        Assert.NotNull(run);
        Assert.Equal("Completed", run.Status);
        Assert.Equal("supervisor", run.Agent);
        Assert.True(run.IsGrounded, JsonSerializer.Serialize(run));
        Assert.Contains("### knowledge", run.Answer, StringComparison.Ordinal);
        Assert.Contains("### document-analyst", run.Answer, StringComparison.Ordinal);
        Assert.NotEmpty(run.Citations);
    }

    [Fact]
    public async Task ConversationIsNotVisibleToAnotherTenant()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/conversations", new CreateConversationRequest("Tenant isolation"));
        response.EnsureSuccessStatusCode();
        var conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("travel limit"))
        };
        request.Headers.Add("Idempotency-Key", "tenant-isolation-key");
        request.Headers.Add("X-Tenant-Id", "other-tenant");
        var result = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task SwaggerUiAndDocumentDescribeTheAgentApi()
    {
        var ui = await _client.GetAsync("/swagger/index.html");
        ui.EnsureSuccessStatusCode();
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var document = await _client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/conversations/{conversationId}/messages", out var messagePath));
        var operation = messagePath.GetProperty("post");
        Assert.True(operation.TryGetProperty("summary", out _));
        var tenantHeader = operation.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "X-Tenant-Id");
        Assert.Equal("demo-tenant", tenantHeader.GetProperty("schema").GetProperty("default").GetString());
    }

    [Fact]
    public async Task StateChangingToolRequiresSingleUseApprovalBeforeExecution()
    {
        var conversationResponse = await _client.PostAsJsonAsync(
            "/api/v1/conversations",
            new CreateConversationRequest("Approval flow"));
        conversationResponse.EnsureSuccessStatusCode();
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("Create a support ticket for a broken demo laptop."))
        };
        message.Headers.Add("Idempotency-Key", "approval-flow-1");
        var proposedResponse = await _client.SendAsync(message);
        Assert.True(proposedResponse.IsSuccessStatusCode, await proposedResponse.Content.ReadAsStringAsync());
        var proposed = await proposedResponse.Content.ReadFromJsonAsync<AgentRunResponse>();

        Assert.NotNull(proposed);
        Assert.Equal("PendingApproval", proposed.Status);
        Assert.Equal("workflow", proposed.Agent);
        Assert.NotNull(proposed.Approval);
        Assert.Contains("not been executed", proposed.Answer, StringComparison.OrdinalIgnoreCase);

        var pending = await _client.GetFromJsonAsync<ApprovalResponse[]>("/api/v1/approvals");
        Assert.Contains(pending ?? [], item => item.ApprovalId == proposed.Approval.ApprovalId);

        using var crossTenant = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/approvals/{proposed.Approval.ApprovalId}/approve")
        {
            Content = JsonContent.Create(new ApproveApprovalRequest(proposed.Approval.ApprovalToken))
        };
        crossTenant.Headers.Add("X-Tenant-Id", "other-tenant");
        var crossTenantResponse = await _client.SendAsync(crossTenant);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantResponse.StatusCode);

        using var missingPermission = new HttpRequestMessage(HttpMethod.Get, "/api/v1/approvals");
        missingPermission.Headers.Add("X-Permissions", "tickets.write");
        var forbidden = await _client.SendAsync(missingPermission);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var invalid = await _client.PostAsJsonAsync(
            $"/api/v1/approvals/{proposed.Approval.ApprovalId}/approve",
            new ApproveApprovalRequest("invalid-token"));
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);

        var approvedResponse = await _client.PostAsJsonAsync(
            $"/api/v1/approvals/{proposed.Approval.ApprovalId}/approve",
            new ApproveApprovalRequest(proposed.Approval.ApprovalToken));
        Assert.True(approvedResponse.IsSuccessStatusCode, await approvedResponse.Content.ReadAsStringAsync());
        var approved = await approvedResponse.Content.ReadFromJsonAsync<ApprovalDecisionResponse>();

        Assert.NotNull(approved);
        Assert.Equal("Executed", approved.Approval.Status);
        Assert.Equal("Completed", approved.Run.Status);
        Assert.Contains("TKT-", approved.Run.Answer, StringComparison.Ordinal);

        var replayApproval = await _client.PostAsJsonAsync(
            $"/api/v1/approvals/{proposed.Approval.ApprovalId}/approve",
            new ApproveApprovalRequest(proposed.Approval.ApprovalToken));
        Assert.Equal(HttpStatusCode.Conflict, replayApproval.StatusCode);
    }

    [Fact]
    public async Task CompletedRunAcceptsOnlyOneValidatedFeedbackItem()
    {
        var conversationResponse = await _client.PostAsJsonAsync(
            "/api/v1/conversations",
            new CreateConversationRequest("Feedback flow"));
        conversationResponse.EnsureSuccessStatusCode();
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("What is the international travel daily expense limit?"))
        };
        message.Headers.Add("Idempotency-Key", "feedback-flow-1");
        var runResponse = await _client.SendAsync(message);
        runResponse.EnsureSuccessStatusCode();
        var run = await runResponse.Content.ReadFromJsonAsync<AgentRunResponse>();
        Assert.NotNull(run);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/runs/{run.RunId}/feedback",
            new SubmitFeedbackRequest(1, "Grounded and useful."));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var feedback = await response.Content.ReadFromJsonAsync<FeedbackResponse>();
        Assert.NotNull(feedback);
        Assert.Equal(run.RunId, feedback.RunId);
        Assert.Equal(1, feedback.Rating);

        var duplicate = await _client.PostAsJsonAsync(
            $"/api/v1/runs/{run.RunId}/feedback",
            new SubmitFeedbackRequest(-1, "Duplicate"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task PendingApprovalRunCanBeCancelledAndCannotExecuteAfterwards()
    {
        var conversationResponse = await _client.PostAsJsonAsync(
            "/api/v1/conversations",
            new CreateConversationRequest("Cancellation flow"));
        conversationResponse.EnsureSuccessStatusCode();
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.NotNull(conversation);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/conversations/{conversation.Id}/messages")
        {
            Content = JsonContent.Create(new SendMessageRequest("Create a support ticket for a cancelled request."))
        };
        message.Headers.Add("Idempotency-Key", "cancellation-flow-1");
        var proposalResponse = await _client.SendAsync(message);
        proposalResponse.EnsureSuccessStatusCode();
        var proposal = await proposalResponse.Content.ReadFromJsonAsync<AgentRunResponse>();
        Assert.NotNull(proposal?.Approval);

        var cancelResponse = await _client.PostAsync($"/api/v1/runs/{proposal.RunId}/cancel", null);
        cancelResponse.EnsureSuccessStatusCode();
        var cancelled = await cancelResponse.Content.ReadFromJsonAsync<AgentRunResponse>();
        Assert.NotNull(cancelled);
        Assert.Equal("Cancelled", cancelled.Status);

        var approvalResponse = await _client.PostAsJsonAsync(
            $"/api/v1/approvals/{proposal.Approval.ApprovalId}/approve",
            new ApproveApprovalRequest(proposal.Approval.ApprovalToken));
        Assert.Equal(HttpStatusCode.Conflict, approvalResponse.StatusCode);
    }
}
