namespace Orchestrator.Domain;

public enum ErrorCategory
{
    Validation,
    Authentication,
    Authorization,
    PolicyDenied,
    NotFound,
    Conflict,
    ProviderUnavailable,
    Timeout,
    InvalidModelOutput,
    ExternalDependency,
    Internal
}

public sealed class DomainException : Exception
{
    public DomainException(ErrorCategory category, string code, string safeMessage, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Category = category;
        Code = code;
        SafeMessage = safeMessage;
    }

    public ErrorCategory Category { get; }
    public string Code { get; }
    public string SafeMessage { get; }
}
