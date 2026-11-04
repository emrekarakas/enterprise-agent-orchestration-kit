using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orchestrator.Application;

namespace Orchestrator.Providers;

public sealed class TicketCreationMockTool : IAgentTool
{
    private static readonly HashSet<string> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Low", "Normal", "High", "Critical"
    };
    private readonly IClock _clock;

    public TicketCreationMockTool(IClock clock) => _clock = clock;

    public ToolDescriptor Descriptor { get; } = new(
        "ticket-create",
        "Creates a synthetic support ticket after explicit human approval.",
        "1.0.0",
        """{"type":"object","required":["subject","description","priority"],"properties":{"subject":{"type":"string","minLength":3,"maxLength":120},"description":{"type":"string","minLength":3,"maxLength":2000},"priority":{"type":"string","enum":["Low","Normal","High","Critical"]}},"additionalProperties":false}""",
        """{"type":"object","required":["ticketId","status","createdAt"],"properties":{"ticketId":{"type":"string"},"status":{"const":"Created"},"createdAt":{"type":"string","format":"date-time"}},"additionalProperties":false}""",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tickets.write" },
        ToolRiskLevel.High,
        ToolMutationBehavior.StateChanging,
        TimeSpan.FromSeconds(5),
        0,
        ToolIdempotencyBehavior.Required);

    public bool ValidateArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !TryReadString(arguments, "subject", 3, 120, out _)
            || !TryReadString(arguments, "description", 3, 2_000, out _)
            || !TryReadString(arguments, "priority", 3, 20, out var priority))
        {
            return false;
        }

        return Priorities.Contains(priority!);
    }

    public Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            return Task.FromResult(new ToolResult(
                false,
                JsonSerializer.SerializeToElement(new { }),
                "tool.idempotency_required",
                false));
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.IdempotencyKey)));
        var output = JsonSerializer.SerializeToElement(new
        {
            ticketId = $"TKT-{digest[..10]}",
            status = "Created",
            createdAt = _clock.UtcNow
        });
        return Task.FromResult(new ToolResult(true, output, null, false));
    }

    public bool ValidateResult(ToolResult result) =>
        result.Succeeded
        && result.Output.ValueKind == JsonValueKind.Object
        && result.Output.TryGetProperty("ticketId", out var ticketId)
        && ticketId.ValueKind == JsonValueKind.String
        && result.Output.TryGetProperty("status", out var status)
        && status.GetString() == "Created";

    private static bool TryReadString(
        JsonElement arguments,
        string name,
        int minimumLength,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()?.Trim();
        return value is not null && value.Length >= minimumLength && value.Length <= maximumLength;
    }
}
