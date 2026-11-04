using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Contracts;
using Orchestrator.Domain;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/documents")]
[DemoIdentityHeaders]
public sealed class DocumentsController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(
        [".pdf", ".docx", ".pptx", ".xlsx", ".md", ".txt"],
        StringComparer.OrdinalIgnoreCase);
    private readonly IDocumentIngestionGateway _documents;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;

    public DocumentsController(
        IDocumentIngestionGateway documents,
        IAuditLog audit,
        IClock clock)
    {
        _documents = documents;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Upload, extract, chunk and index a tenant-scoped document.</summary>
    /// <remarks>
    /// Supported formats: PDF, DOCX, PPTX, XLSX, Markdown and UTF-8 text.
    /// Re-uploading the same source and content hash is safely skipped.
    /// </remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(26_214_400)]
    [ProducesResponseType<DocumentIngestionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<DocumentIngestionResponse>> Upload(
        [FromForm] DocumentUploadForm form,
        CancellationToken cancellationToken)
    {
        var user = HttpContext.GetUserContext();
        RequireWritePermission(user);
        Validate(form.File);
        var accessTags = ParseAccessTags(form.AccessTags, user.AccessTags);
        await using var content = form.File.OpenReadStream();
        var result = await _documents.UploadAsync(new DocumentUpload(
            user.TenantId,
            user.UserId,
            form.File.FileName,
            form.File.ContentType ?? "application/octet-stream",
            content,
            form.File.Length,
            accessTags,
            form.SourceUri), cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(
            Guid.NewGuid(), user.TenantId, user.UserId, "document.upload",
            "document", result.DocumentId, result.Status, _clock.UtcNow,
            user.CorrelationId,
            new Dictionary<string, string>
            {
                ["ingestionId"] = result.IngestionId,
                ["contentHashPrefix"] = result.ContentHash[..Math.Min(12, result.ContentHash.Length)]
            }), cancellationToken).ConfigureAwait(false);
        var response = result.ToResponse();
        return Created($"/api/v1/documents/ingestions/{result.IngestionId}", response);
    }

    /// <summary>Get a tenant-scoped ingestion status.</summary>
    [HttpGet("ingestions/{ingestionId}")]
    [ProducesResponseType<DocumentIngestionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentIngestionResponse>> GetStatus(
        [FromRoute] string ingestionId,
        CancellationToken cancellationToken)
    {
        var user = HttpContext.GetUserContext();
        var result = await _documents.GetAsync(user.TenantId, ingestionId, cancellationToken)
            .ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result.ToResponse());
    }

    /// <summary>Delete a document and its indexed chunks for the current tenant.</summary>
    [HttpDelete("{documentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute] string documentId,
        CancellationToken cancellationToken)
    {
        var user = HttpContext.GetUserContext();
        RequireWritePermission(user);
        var deleted = await _documents.DeleteAsync(user.TenantId, documentId, cancellationToken)
            .ConfigureAwait(false);
        if (!deleted)
        {
            return NotFound();
        }

        await _audit.AppendAsync(new AuditEntry(
            Guid.NewGuid(), user.TenantId, user.UserId, "document.delete", "document",
            documentId, "Deleted", _clock.UtcNow, user.CorrelationId,
            new Dictionary<string, string>()), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static void RequireWritePermission(UserContext user)
    {
        if (!user.Permissions.Contains("documents.write"))
        {
            throw new DomainException(
                ErrorCategory.Authorization,
                "documents.write_denied",
                "The documents.write permission is required.");
        }
    }

    private static void Validate(IFormFile file)
    {
        if (file.Length <= 0)
        {
            throw new DomainException(ErrorCategory.Validation, "document.empty", "A non-empty file is required.");
        }

        if (file.Length > 25 * 1024 * 1024)
        {
            throw new DomainException(ErrorCategory.Validation, "document.too_large", "The maximum upload size is 25 MB.");
        }

        var fileName = file.FileName;
        if (fileName.Length > 200
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\']) >= 0
            || !AllowedExtensions.Contains(Path.GetExtension(fileName)))
        {
            throw new DomainException(ErrorCategory.Validation, "document.invalid_name", "The file name or extension is invalid.");
        }
    }

    private static HashSet<string> ParseAccessTags(
        string? value,
        IReadOnlySet<string> userTags)
    {
        var requested = string.IsNullOrWhiteSpace(value)
            ? userTags.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0 || requested.Any(tag => !userTags.Contains(tag)))
        {
            throw new DomainException(
                ErrorCategory.Authorization,
                "document.access_tags_denied",
                "Document access tags must be a non-empty subset of the uploader's access tags.");
        }

        return requested;
    }
}

public sealed class DocumentUploadForm
{
    public required IFormFile File { get; init; }
    public string? AccessTags { get; init; }
    public string? SourceUri { get; init; }
}
