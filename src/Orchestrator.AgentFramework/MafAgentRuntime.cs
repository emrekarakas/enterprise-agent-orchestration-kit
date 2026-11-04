using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.AgentFramework;

public sealed class MafAgentRuntime : IAgentRuntime
{
    private const string RouteSchema = """
        {"type":"object","required":["agentNames","runInParallel","maxSteps","reasonCode"],"properties":{"agentNames":{"type":"array","minItems":1,"maxItems":4,"items":{"type":"string"}},"runInParallel":{"type":"boolean"},"maxSteps":{"type":"integer","minimum":1,"maximum":8},"reasonCode":{"type":"string"}},"additionalProperties":false}
        """;

    private static readonly ActivitySource ActivitySource = new("Enterprise.AgentKit");
    private readonly IStructuredCompletionProvider _structuredCompletion;
    private readonly AgentRegistry _agents;

    public MafAgentRuntime(IStructuredCompletionProvider structuredCompletion, AgentRegistry agents)
    {
        _structuredCompletion = structuredCompletion;
        _agents = agents;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        PreparedAgentRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("agent.workflow", ActivityKind.Internal);
        activity?.SetTag("agent.framework", "microsoft-agent-framework");
        activity?.SetTag("agent.intent", request.Intent.Intent.ToString());

        var supervisor = new FunctionExecutor<PreparedAgentRequest, RoutedRequest>(
            "supervisor",
            async (input, _, token) => await RouteAsync(input, token).ConfigureAwait(false));
        var specialist = new FunctionExecutor<RoutedRequest, AgentExecutionResult>(
            "specialist-dispatch",
            async (input, _, token) => await ExecuteSpecialistsAsync(input, token).ConfigureAwait(false));

        var workflow = new WorkflowBuilder(supervisor)
            .AddEdge(supervisor, specialist)
            .WithOutputFrom(specialist)
            .WithName("enterprise-agent-run")
            .WithDescription("Typed supervisor-to-specialist execution workflow")
            .Build();

        await using var run = await InProcessExecution.RunAsync(
            workflow,
            request,
            sessionId: request.Original.ConversationId.ToString(),
            cancellationToken).ConfigureAwait(false);

        var workflowEvents = run.NewEvents.ToArray();
        foreach (var workflowEvent in workflowEvents.Reverse())
        {
            if (TryReadResult(workflowEvent.Data, out var result))
            {
                return result;
            }
        }

        var failure = workflowEvents.OfType<ExecutorFailedEvent>().LastOrDefault();
        if (failure?.Data is DomainException domainException)
        {
            throw domainException;
        }

        if (failure?.Data is Exception exception)
        {
            throw new DomainException(
                ErrorCategory.Internal,
                "workflow.executor_failed",
                "The agent workflow could not complete.",
                exception);
        }

        throw new DomainException(
            ErrorCategory.InvalidModelOutput,
            "workflow.no_output",
            "The agent workflow completed without a valid typed output.");
    }

    private static bool TryReadResult(object? data, out AgentExecutionResult result)
    {
        if (data is AgentExecutionResult directResult)
        {
            result = directResult;
            return true;
        }

        if (data is PortableValue portableValue
            && portableValue.Is<AgentExecutionResult>(out var portableResult))
        {
            result = portableResult;
            return true;
        }

        result = null!;
        return false;
    }

    private async ValueTask<RoutedRequest> RouteAsync(PreparedAgentRequest request, CancellationToken cancellationToken)
    {
        var fallbackAgent = request.Intent.Intent switch
        {
            IntentCategory.Workflow => "workflow",
            IntentCategory.DocumentAnalysis => "document-analyst",
            _ => "knowledge"
        };
        var fallback = new RouteDecision([fallbackAgent], false, 4, "safe_fallback");
        var completion = await _structuredCompletion.CompleteAsync(
            new StructuredCompletionRequest<RouteDecision>("supervisor.route", request.RedactedInput, RouteSchema, fallback),
            cancellationToken).ConfigureAwait(false);
        var decision = completion.Value;

        if (decision.AgentNames.Count is < 1 or > 4 || decision.MaxSteps is < 1 or > 8)
        {
            decision = fallback;
        }

        var selected = decision.AgentNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0 || selected.Any(name => !_agents.IsRegistered(name)))
        {
            Activity.Current?.SetTag("agent.route.fallback", true);
            Activity.Current?.SetTag("agent.route.invalid_selection", string.Join(',', selected));
            decision = fallback;
            selected = [fallbackAgent];
        }

        Activity.Current?.SetTag("agent.route.count", selected.Length);
        Activity.Current?.SetTag("agent.route.parallel", decision.RunInParallel && selected.Length > 1);
        return new RoutedRequest(request, selected, decision.RunInParallel, decision.MaxSteps);
    }

    private async ValueTask<AgentExecutionResult> ExecuteSpecialistsAsync(
        RoutedRequest routed,
        CancellationToken cancellationToken)
    {
        AgentExecutionResult[] results;
        if (routed.RunInParallel && routed.AgentNames.Count > 1)
        {
            results = await Task.WhenAll(routed.AgentNames.Select(name =>
                    _agents.GetRequired(name).ExecuteAsync(routed.Request, cancellationToken)))
                .ConfigureAwait(false);
        }
        else
        {
            var sequentialResults = new List<AgentExecutionResult>(routed.AgentNames.Count);
            foreach (var name in routed.AgentNames)
            {
                sequentialResults.Add(await _agents.GetRequired(name)
                    .ExecuteAsync(routed.Request, cancellationToken)
                    .ConfigureAwait(false));
            }

            results = sequentialResults.ToArray();
        }

        var totalSteps = results.Sum(result => result.Steps);
        if (totalSteps > routed.MaxSteps)
        {
            throw new DomainException(ErrorCategory.PolicyDenied, "agent.max_steps", "The agent workflow exceeded its maximum step count.");
        }

        return results.Length == 1 ? results[0] : Combine(results, totalSteps);
    }

    private static AgentExecutionResult Combine(IReadOnlyList<AgentExecutionResult> results, int totalSteps)
    {
        var approvals = results.Where(result => result.Approval is not null).Select(result => result.Approval!).ToArray();
        if (approvals.Length > 1)
        {
            throw new DomainException(
                ErrorCategory.PolicyDenied,
                "agent.multiple_approvals",
                "A supervisor run cannot produce more than one pending approval.");
        }

        var citations = new List<Citation>();
        var sections = new List<string>();
        foreach (var result in results.Where(result => !result.Answer.IsNoAnswer))
        {
            var text = result.Answer.Text;
            foreach (var citation in result.Answer.Citations)
            {
                var nextId = $"[{citations.Count + 1}]";
                text = text.Replace(citation.CitationId, nextId, StringComparison.Ordinal);
                citations.Add(citation with { CitationId = nextId });
            }

            sections.Add($"### {result.AgentName}\n{text}");
        }

        var isNoAnswer = sections.Count == 0;
        var answer = new AgentAnswer(
            isNoAnswer
                ? "The selected specialists could not find sufficient authorized evidence to answer this request."
                : string.Join("\n\n", sections),
            citations,
            !isNoAnswer && results.Where(result => !result.Answer.IsNoAnswer).All(result => result.Answer.IsGrounded),
            isNoAnswer);
        var evidence = results
            .SelectMany(result => result.Evidence)
            .GroupBy(hit => (hit.DocumentId, hit.ChunkId))
            .Select(group => group.First())
            .ToArray();
        var tools = results.SelectMany(result => result.UsedTools).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new AgentExecutionResult("supervisor", answer, evidence, tools, totalSteps, approvals.SingleOrDefault());
    }

    private sealed record RoutedRequest(
        PreparedAgentRequest Request,
        IReadOnlyList<string> AgentNames,
        bool RunInParallel,
        int MaxSteps);
}
