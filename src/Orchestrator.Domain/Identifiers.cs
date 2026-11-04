namespace Orchestrator.Domain;

public readonly record struct TenantId(string Value)
{
    public static TenantId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
        {
            throw new DomainException(ErrorCategory.Validation, "tenant.invalid", "A valid tenant identifier is required.");
        }

        return new TenantId(value.Trim());
    }

    public override string ToString() => Value;
}

public readonly record struct UserId(string Value)
{
    public static UserId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            throw new DomainException(ErrorCategory.Validation, "user.invalid", "A valid user identifier is required.");
        }

        return new UserId(value.Trim());
    }

    public override string ToString() => Value;
}

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct RunId(Guid Value)
{
    public static RunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ApprovalId(Guid Value)
{
    public static ApprovalId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}
