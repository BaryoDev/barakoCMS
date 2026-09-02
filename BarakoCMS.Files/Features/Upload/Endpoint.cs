using FastEndpoints;
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
/// POST /api/files — upload a single file (image or PDF). Bytes go through the configured
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

    public Endpoint(IDocumentSession session, IFileStorage storage)
    {
        _session = session;
        _storage = storage;
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
}
