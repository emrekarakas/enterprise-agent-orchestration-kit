using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Infrastructure;

public sealed class AzureServiceBusOptions
{
    public const string SectionName = "AzureServiceBus";

    public string FullyQualifiedNamespace { get; set; } = string.Empty;
    public string TopicName { get; set; } = "agent-events";
    public string SubscriptionName { get; set; } = "orchestrator";
    public string CredentialMode { get; set; } = "DefaultAzureCredential";
    public string? ManagedIdentityClientId { get; set; }
    public string? ConnectionString { get; set; }
}

public static class AzureServiceBusClientFactory
{
    public static ServiceBusClient Create(AzureServiceBusOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new ServiceBusClient(options.ConnectionString);
        }

        if (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
        {
            throw new InvalidOperationException("AzureServiceBus:FullyQualifiedNamespace is required.");
        }

        TokenCredential credential = string.Equals(
            options.CredentialMode,
            "ManagedIdentity",
            StringComparison.OrdinalIgnoreCase)
            ? new ManagedIdentityCredential(string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId))
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = true,
                ManagedIdentityClientId = options.ManagedIdentityClientId
            });
        return new ServiceBusClient(options.FullyQualifiedNamespace, credential);
    }
}

/// <summary>
/// Keeps low-latency SSE reads in the process-local broker while publishing a redacted event envelope
/// to Azure Service Bus for durable downstream consumers.
/// </summary>
public sealed class AzureServiceBusRunEventBroker : IRunEventBroker
{
    private readonly IRunEventBroker _localBroker;
    private readonly ServiceBusSender _sender;

    public AzureServiceBusRunEventBroker(
        IRunEventBroker localBroker,
        ServiceBusClient client,
        IOptions<AzureServiceBusOptions> options)
    {
        _localBroker = localBroker;
        _sender = client.CreateSender(options.Value.TopicName);
    }

    public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        await _localBroker.PublishAsync(runEvent, cancellationToken).ConfigureAwait(false);
        var envelope = new
        {
            runId = runEvent.RunId.Value,
            tenantId = runEvent.TenantId.Value,
            runEvent.Type,
            runEvent.OccurredAt,
            runEvent.Data
        };
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
        {
            ContentType = "application/json",
            Subject = runEvent.Type,
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = runEvent.RunId.ToString()
        };
        message.ApplicationProperties["tenantId"] = runEvent.TenantId.Value;
        message.ApplicationProperties["runId"] = runEvent.RunId.ToString();
        await _sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<RunEvent> ReadAsync(
        TenantId tenantId,
        RunId runId,
        long afterSequence,
        CancellationToken cancellationToken) =>
        ReadLocalAsync(tenantId, runId, afterSequence, cancellationToken);

    private async IAsyncEnumerable<RunEvent> ReadLocalAsync(
        TenantId tenantId,
        RunId runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _localBroker.ReadAsync(tenantId, runId, afterSequence, cancellationToken))
        {
            yield return item;
        }
    }
}
