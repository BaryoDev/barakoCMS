using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Marten;

namespace BarakoCMS.Files.Features.Upload;

public class Response
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>Direct public URL for a public file on an object store; null for Postgres-stored files
    /// (fetch those via GET /api/public/files/{id}).</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>
/// POST /api/files. Uploads a single file (image or PDF). Bytes go through the configured
/// <see cref="IFileStorage"/> (Postgres or S3); metadata is recorded in Postgres. Pass a form field
/// <c>isPublic=true</c> to make it anonymously readable; the default is private (fail closed).
/// </summary>
public class Endpoint : EndpointWithoutRequest<Response>
{
    private const long MaxBytes = 10L * 1024 * 1024;

    // Explicit allow-list of raster image types + PDF. SVG is deliberately excluded: it is XML that can
    // carry <script>, so a public SVG opened directly would run JS on the API origin. Callers who need
    // vector art can reference an external URL instead.
    private static readonly string[] Allowed =
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "application/pdf",
    };

    private readonly IDocumentSession _session;
    private readonly IFileStorage _storage;
    private readonly IFileScanner _scanner;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        IFileStorage storage,
        IFileScanner scanner,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _storage = storage;
        _scanner = scanner;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/files");
        // Was authenticated and nothing else, so every self-registered User-role account could
        // store 10 MB per call into the tenant and mark it public, producing an anonymously
        // readable URL on the deployment's own domain. Gated to match the rest of the write
        // surface. A per-user quota is the separate question (#138 covers scanning).
        Roles("SuperAdmin", "Admin");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var file = Files.Count > 0 ? Files[0] : null;
        if (file is null || file.Length == 0)
        {
            AddError("A file is required.");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (file.Length > MaxBytes)
        {
            AddError($"File is too large (max {MaxBytes / (1024 * 1024)} MB).");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        var contentType = file.ContentType ?? "application/octet-stream";
        if (!Allowed.Any(a => contentType.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            AddError("Only images and PDF files are allowed.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var isPublic = HttpContext.Request.Form.TryGetValue("isPublic", out var pub)
                       && string.Equals(pub.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        /* Key keeps the extension so an object store serves the right content type via the URL. */
        var ext = Path.GetExtension(file.FileName);
        var key = $"{Guid.NewGuid():N}{ext}";

        if (_scanner.Configured)
        {
            // Its own read of the file, finished before storage opens its own. The stream is
            // forward-only, so scanning and storing cannot share one, and buffering ten megabytes per
            // request to avoid a second open is the worse trade.
            ScanResult scan;
            await using (var forScanning = file.OpenReadStream())
            {
                scan = await _scanner.ScanAsync(forScanning, ct);
            }

            if (scan.Verdict != ScanVerdict.Clean)
            {
                await RecordRefusalAsync(file, contentType, userId, scan, ct);

                // Refused either way, and the two are told apart in the message rather than in the
                // outcome. An unreachable scanner is the operator's problem and a person waiting on
                // it should be able to say which happened, but neither is a reason to store the file:
                // "the scanner was down" is not evidence that a file is safe.
                AddError(scan.Verdict == ScanVerdict.Infected
                    ? $"This file was refused by the virus scanner ({scan.Signature})."
                    : "This file could not be scanned, so it was not stored. Try again shortly.");

                await Send.ErrorsAsync(scan.Verdict == ScanVerdict.Infected ? 422 : 503, ct);
                return;
            }
        }

        await using var stream = file.OpenReadStream();
        var stored = await _storage.PutAsync(stream, key, contentType, isPublic, ct);

        var record = new StoredFile
        {
            FileName = Path.GetFileName(file.FileName),
            ContentType = contentType,
            Size = file.Length,
            Provider = _storage.Provider,
            StorageKey = stored.Key,
            IsPublic = isPublic,
            PublicUrl = stored.PublicUrl,
            UploadedBy = userId,
        };
        _session.Store(record);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response
        {
            Id = record.Id,
            FileName = record.FileName,
            ContentType = record.ContentType,
            Size = record.Size,
            IsPublic = record.IsPublic,
            PublicUrl = record.PublicUrl,
        }, 201, ct);
    }

    /// <summary>
    /// Records what was refused and why, in the audit log rather than in a quarantine of its own.
    /// </summary>
    /// <remarks>
    /// "Quarantine" usually means keeping the file somewhere an operator can look at it. That is not
    /// what happens here and the difference is deliberate: the file is never stored, so there is
    /// nothing on this deployment to serve by accident or to forget to clean up. What an operator
    /// needs is the record, which is the name, the size, who sent it and what the scanner called it,
    /// and the audit log already is that record. It is hash chained and it already has a screen, so
    /// a refusal cannot be quietly removed from it either.
    ///
    /// Committed on its own, before the error response. It has to survive a request that ends in a
    /// 4xx, and nothing else in this handler has staged a write by this point.
    /// </remarks>
    private async Task RecordRefusalAsync(
        IFormFile file, string contentType, Guid userId, ScanResult scan, CancellationToken ct)
    {
        await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
            _session,
            _tenant.Slug,
            scan.Verdict == ScanVerdict.Infected ? "file.refused.infected" : "file.refused.unscanned",
            userId,
            User.FindFirst("Username")?.Value ?? string.Empty,
            targetType: "file",
            targetId: Path.GetFileName(file.FileName),
            metadata: new Dictionary<string, object>
            {
                ["fileName"] = Path.GetFileName(file.FileName) ?? string.Empty,
                ["contentType"] = contentType,
                ["size"] = file.Length,
                // One or the other, never both, and never the file's bytes.
                ["reason"] = scan.Signature ?? scan.Error ?? "unknown",
            },
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        await _session.SaveChangesAsync(ct);
    }
}
