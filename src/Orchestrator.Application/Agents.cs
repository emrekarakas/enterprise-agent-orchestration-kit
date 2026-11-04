using Orchestrator.Domain;

namespace Orchestrator.Application;

public sealed record AgentDescriptor(
    string Name,
    string Description,
    string Version,
    IReadOnlySet<IntentCategory> SupportedIntents,
    int MaxSteps,
    TimeSpan Timeout);

public interface IAgentDefinition
{
    AgentDescriptor Descriptor { get; }
    Task<AgentExecutionResult> ExecuteAsync(PreparedAgentRequest request, CancellationToken cancellationToken);
}

public sealed class AgentRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentDefinition> _agents;

    public AgentRegistry(IEnumerable<IAgentDefinition> agents)
    {
        var dictionary = new Dictionary<string, IAgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            if (!dictionary.TryAdd(agent.Descriptor.Name, agent))
            {
                throw new InvalidOperationException($"Duplicate agent registration: {agent.Descriptor.Name}");
            }
        }

        _agents = dictionary;
    }

    public IReadOnlyCollection<AgentDescriptor> List() => _agents.Values.Select(agent => agent.Descriptor).ToArray();

    public bool IsRegistered(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _agents.ContainsKey(name);

    public IAgentDefinition GetRequired(string name)
    {
        if (!_agents.TryGetValue(name, out var agent))
        {
            throw new DomainException(ErrorCategory.InvalidModelOutput, "agent.unknown", "The selected agent is not registered.");
        }

        return agent;
    }
}
