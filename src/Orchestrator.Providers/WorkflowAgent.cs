using System.Diagnostics;
using System.Text.Json;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class WorkflowAgent : IAgentDefinition
{
    private const string ProposalSchema = """
        {"type":"object","required":["subject","description","priority"],"properties":{"subject":{"type":"string","minLength":3,"maxLength":120},"description":{"type":"string","minLength":3,"maxLength":2000},"priority":{"type":"string","enum":["Low","Normal","High","Critical"]}},"additionalProperties":false}
        """;
    private static readonly ActivitySource ActivitySource = new("Enterprise.AgentKit");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IStructuredCompletionProvider _structuredCompletion;
    private readonly ApprovalService _approvals;

    public WorkflowAgent(IStructuredCompletionProvider structuredCompletion, ApprovalService approvals)
    {
        _structuredCompletion = structuredCompletion;
        _approvals = approvals;
    }

    public AgentDescriptor Descriptor { get; } = new(
        "workflow",
        "Proposes controlled state-changing operations and pauses for human approval.",
        "1.0.0",
        new HashSet<IntentCategory> { IntentCategory.Workflow },
        4,
        TimeSpan.FromSeconds(20));

    public async Task<AgentExecutionResult> ExecuteAsync(PreparedAgentRequest request, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("agent.workflow-specialist", ActivityKind.Internal);
        activity?.SetTag("gen_ai.agent.name", Descriptor.Name);

        var description = request.RedactedInput.Length <= 2_000
            ? request.RedactedInput
            : request.RedactedInput[..2_000];
        var subject = description.Length <= 120 ? description : description[..120];
        var fallback = new TicketProposal(subject, description, "Normal");
        var completion = await _structuredCompletion.CompleteAsync(
            new StructuredCompletionRequest<TicketProposal>(
                "workflow.ticket_proposal",
                request.RedactedInput,
                ProposalSchema,
                fallback),
            cancellationToken).ConfigureAwait(false);
        var proposal = Normalize(completion.Value, fallback);
        var arguments = JsonSerializer.SerializeToElement(proposal, JsonOptions);
        var challenge = await _approvals.CreateAsync(
            request,
            "ticket-create",
            arguments,
            cancellationToken).ConfigureAwait(false);

        var answer = new AgentAnswer(
            $"A support ticket proposal was prepared with {proposal.Priority.ToLowerInvariant()} priority. It has not been executed and is waiting for human approval.",
            [],
            false,
            false);
        return new AgentExecutionResult(Descriptor.Name, answer, [], [], 2, challenge);
    }

    private static TicketProposal Normalize(TicketProposal proposal, TicketProposal fallback)
    {
        var priority = proposal.Priority is "Low" or "Normal" or "High" or "Critical"
            ? proposal.Priority
            : fallback.Priority;
        var subject = string.IsNullOrWhiteSpace(proposal.Subject) ? fallback.Subject : proposal.Subject.Trim();
        var description = string.IsNullOrWhiteSpace(proposal.Description) ? fallback.Description : proposal.Description.Trim();
        return new TicketProposal(
            subject.Length <= 120 ? subject : subject[..120],
            description.Length <= 2_000 ? description : description[..2_000],
            priority);
    }
}
