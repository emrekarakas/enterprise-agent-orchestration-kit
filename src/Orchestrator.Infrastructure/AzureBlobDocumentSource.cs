using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Orchestrator.Application;

namespace Orchestrator.Infrastructure;

public sealed class AzureBlobStorageOptions
{
    public const string SectionName = "AzureBlobStorage";

    public bool Enabled { get; set; }
    public string ServiceUri { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "eaokit-documents-dev";
    public string CredentialMode { get; set; } = "DefaultAzureCredential";
    public string? ManagedIdentityClientId { get; set; }
    public string? ConnectionString { get; set; }
}

public static class AzureBlobContainerClientFactory
{
    public static BlobContainerClient Create(AzureBlobStorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, options.ContainerName);
        }

        if (!Uri.TryCreate(options.ServiceUri, UriKind.Absolute, out var serviceUri))
        {
            throw new InvalidOperationException("AzureBlobStorage:ServiceUri must be an absolute URI.");
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
        return new BlobServiceClient(serviceUri, credential).GetBlobContainerClient(options.ContainerName);
    }
}

public sealed class AzureBlobDocumentSource : IDocumentSource
{
    private readonly BlobContainerClient _container;

    public AzureBlobDocumentSource(
        BlobContainerClient container,
        IOptions<AzureBlobStorageOptions> options)
    {
        _container = container;
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.ContainerName))
        {
            throw new InvalidOperationException("Azure Blob container name is required.");
        }
    }

    public async Task<IReadOnlyList<SourceDocument>> ListAsync(
        string? prefix,
        CancellationToken cancellationToken)
    {
        var result = new List<SourceDocument>();
        await foreach (var blob in _container.GetBlobsAsync(
            BlobTraits.Metadata,
            BlobStates.None,
            string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(),
            cancellationToken))
        {
            result.Add(new SourceDocument(
                blob.Name,
                _container.GetBlobClient(blob.Name).Uri.ToString(),
                Path.GetFileName(blob.Name),
                blob.Properties.ETag?.ToString() ?? string.Empty,
                blob.Properties.ContentLength ?? 0,
                blob.Properties.LastModified,
                blob.Metadata?.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>()));
        }

        return result;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Contains('\0'))
        {
            throw new ArgumentException("A valid blob key is required.", nameof(key));
        }

        var response = await _container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Value.Content;
    }
}
