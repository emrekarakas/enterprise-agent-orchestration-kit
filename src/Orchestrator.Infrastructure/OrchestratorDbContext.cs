using Microsoft.EntityFrameworkCore;

namespace Orchestrator.Infrastructure;

public sealed class OrchestratorDbContext : DbContext
{
    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConversationRow> Conversations => Set<ConversationRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<IdempotencyRow> IdempotencyKeys => Set<IdempotencyRow>();
    public DbSet<AuditRow> AuditEntries => Set<AuditRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();
    public DbSet<FeedbackRow> Feedback => Set<FeedbackRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationRow>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.CreatedBy).HasMaxLength(200);
            entity.Property(row => row.Title).HasMaxLength(200);
            entity.HasIndex(row => new { row.TenantId, row.CreatedBy, row.UpdatedAt });
        });

        modelBuilder.Entity<MessageRow>(entity =>
        {
            entity.ToTable("conversation_messages");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.Role).HasMaxLength(20);
            entity.Property(row => row.Content).HasMaxLength(16_000);
            entity.HasIndex(row => new { row.TenantId, row.ConversationId, row.CreatedAt });
        });

        modelBuilder.Entity<RunRow>(entity =>
        {
            entity.ToTable("agent_runs");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.UserId).HasMaxLength(200);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.TraceId).HasMaxLength(64);
            entity.HasIndex(row => new { row.TenantId, row.ConversationId, row.CreatedAt });
        });

        modelBuilder.Entity<IdempotencyRow>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(row => new { row.TenantId, row.Key });
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.Key).HasMaxLength(200);
            entity.HasIndex(row => row.ExpiresAt);
        });

        modelBuilder.Entity<AuditRow>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.UserId).HasMaxLength(200);
            entity.Property(row => row.Action).HasMaxLength(100);
            entity.Property(row => row.Outcome).HasMaxLength(50);
            entity.HasIndex(row => new { row.TenantId, row.OccurredAt });
        });

        modelBuilder.Entity<ApprovalRow>(entity =>
        {
            entity.ToTable("approval_requests");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.RequestedBy).HasMaxLength(200);
            entity.Property(row => row.DecidedBy).HasMaxLength(200);
            entity.Property(row => row.ToolName).HasMaxLength(100);
            entity.Property(row => row.ToolVersion).HasMaxLength(50);
            entity.Property(row => row.ArgumentsHash).HasMaxLength(64);
            entity.Property(row => row.TokenHash).HasMaxLength(64);
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
            entity.Property(row => row.Status).HasMaxLength(32);
            entity.Property(row => row.ErrorCode).HasMaxLength(100);
            entity.Property(row => row.RejectionReason).HasMaxLength(500);
            entity.HasIndex(row => new { row.TenantId, row.Status, row.ExpiresAt });
            entity.HasIndex(row => new { row.TenantId, row.RunId }).IsUnique();
        });

        modelBuilder.Entity<FeedbackRow>(entity =>
        {
            entity.ToTable("run_feedback");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.TenantId).HasMaxLength(100);
            entity.Property(row => row.UserId).HasMaxLength(200);
            entity.Property(row => row.Comment).HasMaxLength(2000);
            entity.HasIndex(row => new { row.TenantId, row.RunId, row.UserId }).IsUnique();
        });
    }
}

public sealed class ConversationRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MessageRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? RunId { get; set; }
}

public sealed class RunRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? SelectedAgent { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public string? AnswerJson { get; set; }
}

public sealed class IdempotencyRow
{
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public Guid RunId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
}

public sealed class ApprovalRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid ConversationId { get; set; }
    public Guid RunId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ToolVersion { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string ArgumentsHash { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class FeedbackRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ConversationId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
