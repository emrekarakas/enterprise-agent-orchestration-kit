using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.ArchitectureTests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayersOrAgentFramework()
    {
        var references = typeof(DomainException).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();

        Assert.DoesNotContain("Orchestrator.Application", references);
        Assert.DoesNotContain("Orchestrator.Infrastructure", references);
        Assert.DoesNotContain("Orchestrator.Providers", references);
        Assert.DoesNotContain("Orchestrator.AgentFramework", references);
        Assert.DoesNotContain(references, name => name?.StartsWith("Microsoft.Agents", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureProvidersOrAgentFramework()
    {
        var references = typeof(AgentRunService).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();

        Assert.DoesNotContain("Orchestrator.Infrastructure", references);
        Assert.DoesNotContain("Orchestrator.Providers", references);
        Assert.DoesNotContain("Orchestrator.AgentFramework", references);
        Assert.DoesNotContain(references, name => name?.StartsWith("Microsoft.Agents", StringComparison.Ordinal) == true);
    }
}
