using System.Collections.ObjectModel;
using System.Text.Json;
using Orchestrator.Domain;

namespace Orchestrator.Application;

public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    bool ValidateArguments(JsonElement arguments);
    Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonElement arguments, CancellationToken cancellationToken);
    bool ValidateResult(ToolResult result);
}

public sealed class ToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        var dictionary = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!dictionary.TryAdd(tool.Descriptor.Name, tool))
            {
                throw new InvalidOperationException($"Duplicate tool registration: {tool.Descriptor.Name}");
            }
        }

        _tools = new ReadOnlyDictionary<string, IAgentTool>(dictionary);
    }

    public IReadOnlyCollection<ToolDescriptor> List() => _tools.Values.Select(tool => tool.Descriptor).ToArray();

    public bool TryGet(string name, out IAgentTool? tool) => _tools.TryGetValue(name, out tool);
}

public sealed class ToolDispatcher
{
    private readonly ToolRegistry _registry;

    public ToolDispatcher(ToolRegistry registry) => _registry = registry;

    public async Task<ToolResult> DispatchAsync(
        string toolName,
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(toolName, out var tool) || tool is null)
        {
            throw new DomainException(ErrorCategory.Validation, "tool.unknown", "The requested tool is not registered.");
        }

        if (!context.Request.Policy.AllowedTools.Contains(tool.Descriptor.Name))
        {
            throw new DomainException(ErrorCategory.Authorization, "tool.not_allowed", "The tool is not allowed for this request.");
        }

        if (!tool.Descriptor.RequiredPermissions.IsSubsetOf(context.Request.Original.User.Permissions))
        {
            throw new DomainException(ErrorCategory.Authorization, "tool.permission_denied", "The caller is not authorized to use this tool.");
        }

        if (!tool.ValidateArguments(arguments))
        {
            throw new DomainException(ErrorCategory.Validation, "tool.arguments_invalid", "The tool arguments do not match the registered schema.");
        }

        if (tool.Descriptor.MutationBehavior == ToolMutationBehavior.StateChanging)
        {
            return new ToolResult(false, JsonSerializer.SerializeToElement(new { }), "approval.required", true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(tool.Descriptor.Timeout);
        var result = await tool.ExecuteAsync(context, arguments, timeout.Token).ConfigureAwait(false);
        if (!tool.ValidateResult(result))
        {
            throw new DomainException(ErrorCategory.ExternalDependency, "tool.invalid_result", "The tool returned an invalid result.");
        }

        return result;
    }

    public async Task<ToolResult> DispatchApprovedAsync(
        ApprovalRequest approval,
        UserContext approver,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(approval.ToolName, out var tool) || tool is null)
        {
            throw new DomainException(ErrorCategory.Validation, "tool.unknown", "The approved tool is not registered.");
        }

        if (tool.Descriptor.MutationBehavior != ToolMutationBehavior.StateChanging
            || !string.Equals(tool.Descriptor.Version, approval.ToolVersion, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.tool_changed", "The registered tool no longer matches the approved operation.");
        }

        if (!approver.Permissions.Contains("approvals.approve")
            || !tool.Descriptor.RequiredPermissions.IsSubsetOf(approver.Permissions))
        {
            throw new DomainException(ErrorCategory.Authorization, "approval.permission_denied", "The caller is not authorized to approve and execute this tool.");
        }

        if (!tool.ValidateArguments(arguments))
        {
            throw new DomainException(ErrorCategory.Validation, "tool.arguments_invalid", "The approved tool arguments are invalid.");
        }

        var original = new AgentRequest(
            approval.ConversationId,
            string.Empty,
            approver,
            approval.IdempotencyKey,
            approval.RunId);
        var prepared = new PreparedAgentRequest(
            original,
            string.Empty,
            string.Empty,
            [],
            new IntentDecision(IntentCategory.Workflow, 1, "approved_workflow"),
            InputRisk.None,
            new PolicyDecision(true, "approved", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { approval.ToolName }));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(tool.Descriptor.Timeout);
        var result = await tool.ExecuteAsync(
            new ToolExecutionContext(prepared, approval.Id.ToString(), approval.IdempotencyKey),
            arguments,
            timeout.Token).ConfigureAwait(false);
        if (!tool.ValidateResult(result))
        {
            throw new DomainException(ErrorCategory.ExternalDependency, "tool.invalid_result", "The tool returned an invalid result.");
        }

        return result;
    }
}
