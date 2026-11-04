namespace Orchestrator.IntegrationTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ContainerFactAttribute : FactAttribute
{
    public ContainerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TESTCONTAINERS_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set TESTCONTAINERS_ENABLED=true to run Docker-backed integration tests.";
        }
    }
}
