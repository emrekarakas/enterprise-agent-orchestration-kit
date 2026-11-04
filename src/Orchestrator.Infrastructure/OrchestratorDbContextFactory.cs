using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orchestrator.Infrastructure;

public sealed class OrchestratorDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=agentkit_design;Username=agentkit")
            .Options;
        return new OrchestratorDbContext(options);
    }
}
