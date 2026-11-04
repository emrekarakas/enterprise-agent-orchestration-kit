using System.Text;
using System.Text.RegularExpressions;
using Orchestrator.Domain;

namespace Orchestrator.Application;

public sealed record PreparationContext(
    AgentRequest Request,
    string NormalizedInput,
    string RedactedInput,
    IReadOnlyList<ConversationMessage> RelevantHistory,
    IntentDecision Intent,
    InputRisk InputRisk,
    PolicyDecision Policy)
{
    public static PreparationContext Start(AgentRequest request) => new(
        request,
        string.Empty,
        string.Empty,
        [],
        new IntentDecision(IntentCategory.Unknown, 0, "not_classified"),
        InputRisk.None,
        new PolicyDecision(false, "not_evaluated", new HashSet<string>(StringComparer.Ordinal)));

    public PreparedAgentRequest ToPreparedRequest()
    {
        if (!Policy.Allowed)
        {
            throw new DomainException(ErrorCategory.PolicyDenied, Policy.ReasonCode, "The request was denied by policy.");
        }

        return new PreparedAgentRequest(
            Request,
            NormalizedInput,
            RedactedInput,
            RelevantHistory,
            Intent,
            InputRisk,
            Policy);
    }
}

public interface IRequestPreparationStage
{
    string Name { get; }
    int Order { get; }
    Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken);
}

public sealed class RequestPreparationPipeline
{
    private readonly IReadOnlyList<IRequestPreparationStage> _stages;

    public RequestPreparationPipeline(IEnumerable<IRequestPreparationStage> stages)
    {
        _stages = stages.OrderBy(stage => stage.Order).ToArray();
    }

    public async Task<PreparedAgentRequest> PrepareAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var context = PreparationContext.Start(request);
        foreach (var stage in _stages)
        {
            context = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return context.ToPreparedRequest();
    }
}

public sealed class NormalizeInputStage : IRequestPreparationStage
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    public string Name => "normalize_input";
    public int Order => 100;

    public Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Whitespace.Replace(context.Request.Input.Normalize(NormalizationForm.FormKC).Trim(), " ");
        if (normalized.Length is < 1 or > 8_000)
        {
            throw new DomainException(ErrorCategory.Validation, "request.input_length", "Input must contain between 1 and 8,000 characters.");
        }

        return Task.FromResult(context with { NormalizedInput = normalized, RedactedInput = normalized });
    }
}

public sealed class InputSafetyStage : IRequestPreparationStage
{
    private static readonly string[] BlockedCombinations =
    [
        "ignore previous instructions",
        "reveal system prompt",
        "show hidden instructions",
        "exfiltrate secrets"
    ];

    public string Name => "input_safety";
    public int Order => 200;

    public Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = context.NormalizedInput.ToLowerInvariant();
        var risk = BlockedCombinations.Any(input.Contains) ? InputRisk.Blocked : InputRisk.None;
        return Task.FromResult(context with { InputRisk = risk });
    }
}

public sealed class SecretRedactionStage : IRequestPreparationStage
{
    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex Bearer = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex ApiKey = new(
        @"\b(?:api[_-]?key|secret)\s*[:=]\s*[^\s,;]{6,}",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(50));

    public string Name => "secret_redaction";
    public int Order => 300;

    public Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var redacted = Email.Replace(context.NormalizedInput, "[REDACTED_EMAIL]");
        redacted = Bearer.Replace(redacted, "[REDACTED_TOKEN]");
        redacted = ApiKey.Replace(redacted, "[REDACTED_SECRET]");
        return Task.FromResult(context with { RedactedInput = redacted });
    }
}

public sealed class ConversationContextStage : IRequestPreparationStage
{
    private readonly IConversationStore _store;
    private readonly int _fetchLimit;
    private readonly int _maxMessages;
    private readonly int _maxCharacters;

    public ConversationContextStage(
        IConversationStore store,
        int fetchLimit = 50,
        int maxMessages = 12,
        int maxCharacters = 12_000)
    {
        _store = store;
        _fetchLimit = Math.Max(fetchLimit, maxMessages);
        _maxMessages = Math.Max(2, maxMessages);
        _maxCharacters = Math.Max(1000, maxCharacters);
    }

    public string Name => "conversation_context";
    public int Order => 400;

    public async Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        var conversation = await _store.GetAsync(
            context.Request.User.TenantId,
            context.Request.ConversationId,
            cancellationToken).ConfigureAwait(false);

        if (conversation is null)
        {
            throw new DomainException(ErrorCategory.NotFound, "conversation.not_found", "Conversation was not found.");
        }

        var history = await _store.GetRecentMessagesAsync(
            context.Request.User.TenantId,
            context.Request.ConversationId,
            _fetchLimit,
            cancellationToken).ConfigureAwait(false);

        return context with { RelevantHistory = Compact(history, context.NormalizedInput) };
    }

    private IReadOnlyList<ConversationMessage> Compact(
        IReadOnlyList<ConversationMessage> history,
        string currentInput)
    {
        if (history.Count <= _maxMessages && history.Sum(item => item.Content.Length) <= _maxCharacters)
        {
            return history;
        }

        var queryTerms = currentInput.Split(
                [' ', '\t', '\r', '\n', '.', ',', '?', '!', ':', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recentCount = Math.Min(6, Math.Max(1, (_maxMessages - 1) / 2));
        var recent = history.TakeLast(recentCount).ToArray();
        var recentIds = recent.Select(item => item.Id).ToHashSet();
        var relevantSlots = Math.Max(0, _maxMessages - recent.Length - 1);
        var relevant = history
            .Where(item => !recentIds.Contains(item.Id))
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Score = queryTerms.Count(term => item.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Index)
            .Take(relevantSlots)
            .Select(candidate => candidate.Item)
            .ToArray();
        var selected = relevant.Concat(recent)
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.CreatedAt)
            .ToList();

        while (selected.Count > 2 && selected.Sum(item => item.Content.Length) > _maxCharacters - 1000)
        {
            selected.RemoveAt(0);
        }

        var selectedIds = selected.Select(item => item.Id).ToHashSet();
        var omitted = history.Where(item => !selectedIds.Contains(item.Id)).TakeLast(8).ToArray();
        var summaryText = "Earlier conversation summary: " + string.Join(" | ", omitted.Select(item =>
        {
            var text = item.Content.ReplaceLineEndings(" ");
            var snippet = text[..Math.Min(text.Length, 120)];
            return $"{item.Role}: {snippet}";
        }));
        var summary = new ConversationMessage(
            MessageId.New(),
            history[0].ConversationId,
            history[0].TenantId,
            MessageRole.System,
            summaryText[..Math.Min(summaryText.Length, 1000)],
            omitted.Length > 0 ? omitted[^1].CreatedAt : history[0].CreatedAt);
        return new[] { summary }.Concat(selected).Take(_maxMessages).ToArray();
    }
}

public sealed class IntentClassificationStage : IRequestPreparationStage
{
    private const string Schema = """
        {"type":"object","required":["intent","confidence","reasonCode"],"properties":{"intent":{"type":"string","enum":["Knowledge","DocumentAnalysis","Workflow","Conversation","Unknown"]},"confidence":{"type":"number","minimum":0,"maximum":1},"reasonCode":{"type":"string"}},"additionalProperties":false}
        """;

    private readonly IStructuredCompletionProvider _provider;

    public IntentClassificationStage(IStructuredCompletionProvider provider) => _provider = provider;

    public string Name => "intent_classification";
    public int Order => 500;

    public async Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        var fallback = new IntentDecision(IntentCategory.Knowledge, 0.5, "deterministic_fallback");
        var result = await _provider.CompleteAsync(
            new StructuredCompletionRequest<IntentDecision>("intent.classify", context.RedactedInput, Schema, fallback),
            cancellationToken).ConfigureAwait(false);

        return context with { Intent = result.Value };
    }
}

public sealed class PolicyEvaluationStage : IRequestPreparationStage
{
    private readonly IPolicyEvaluator _policyEvaluator;

    public PolicyEvaluationStage(IPolicyEvaluator policyEvaluator) => _policyEvaluator = policyEvaluator;

    public string Name => "policy_evaluation";
    public int Order => 600;

    public async Task<PreparationContext> ExecuteAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        var decision = await _policyEvaluator.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        return context with { Policy = decision };
    }
}

public sealed class DefaultPolicyEvaluator : IPolicyEvaluator
{
    public Task<PolicyDecision> EvaluateAsync(PreparationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.InputRisk == InputRisk.Blocked)
        {
            return Task.FromResult(new PolicyDecision(false, "input_safety_block", new HashSet<string>(StringComparer.Ordinal)));
        }

        var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.Request.User.Permissions.Contains("knowledge.read"))
        {
            allowedTools.Add("enterprise-search");
        }

        if (context.Request.User.Permissions.Contains("tickets.write"))
        {
            allowedTools.Add("ticket-create");
        }

        return Task.FromResult(new PolicyDecision(true, "allowed", allowedTools));
    }
}
