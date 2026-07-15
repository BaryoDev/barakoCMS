using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Upload;

public class Response
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>
/// POST /api/files — upload a single file (image or PDF), stored in the database.
/// Returns the file id, which callers attach to their own records.
/// </summary>
public class Endpoint : EndpointWithoutRequest<Response>
{
    // Keep DB-stored files small; matches the CMS's default request body limit.
    private const long MaxBytes = 10L * 1024 * 1024;
    private static readonly string[] Allowed = { "image/", "application/pdf" };

    private readonly IDocumentSession _session;
    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/files");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var file = Files.Count > 0 ? Files[0] : null;
        if (file is null || file.Length == 0)
        {
            AddError("A file is required.");
            await SendErrorsAsync(400, ct);
            return;
        }
        if (file.Length > MaxBytes)
        {
            AddError($"File is too large (max {MaxBytes / (1024 * 1024)} MB).");
            await SendErrorsAsync(400, ct);
            return;
        }
        var contentType = file.ContentType ?? "application/octet-stream";
        if (!Allowed.Any(a => contentType.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            AddError("Only images and PDF files are allowed.");
            await SendErrorsAsync(400, ct);
            return;
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var stored = new StoredFile
        {
            FileName = Path.GetFileName(file.FileName),
            ContentType = contentType,
            Size = file.Length,
            Data = ms.ToArray(),
            UploadedBy = userId,
        };
        _session.Store(stored);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response
        {
            Id = stored.Id,
            FileName = stored.FileName,
            ContentType = stored.ContentType,
            Size = stored.Size,
        }, 201, ct);
    }
}
