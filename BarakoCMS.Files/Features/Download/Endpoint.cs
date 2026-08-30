using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Download;

public class Request
{
    public Guid Id { get; set; }
}

/// <summary>
/// GET /api/files/{id} — authenticated download of any file. If the file lives on an object store with
/// a public URL, redirects there; otherwise streams the bytes from the configured storage.
/// </summary>
public class Endpoint : Endpoint<Request>
{
    private readonly IQuerySession _session;
    private readonly IFileStorage _storage;

    public Endpoint(IQuerySession session, IFileStorage storage)
    {
        _session = session;
        _storage = storage;
    }

    public override void Configure()
    {
        Get("/api/files/{id}");
        /* Requires authentication (no AllowAnonymous). Callers fetch with a Bearer token, and the
           handler then decides whether this caller may have this file. */
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null) { await Send.NotFoundAsync(ct); return; }

        // Authentication was the whole check here, so any signed-in account could read any file in
        // the tenant given its id. Ids are GUIDs, so this needed a leaked or logged id rather than a
        // scan, which lowers the severity without making the check optional.
        //
        // Until content can reference a file (#141) there is no richer answer than "the person who
        // uploaded it, or someone administering the tenant". Note that IsPublic is deliberately NOT
        // sufficient here: PublicDownload is the route for public files, it sets its own caching and
        // headers, and honouring the flag on this route too would mean two paths to the same bytes
        // with different rules.
        if (!CanRead(file))
        {
            // 404, not 403, matching PublicDownload: a 403 confirms the id exists, which turns a
            // leaked id into a probe for what else is there.
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!string.IsNullOrEmpty(file.PublicUrl))
        {
            HttpContext.Response.StatusCode = 302;
            HttpContext.Response.Headers.Location = file.PublicUrl;
            return;
        }

        var bytes = await _storage.GetAsync(file.StorageKey, ct);
        if (bytes is null) { await Send.NotFoundAsync(ct); return; }

        await Send.BytesAsync(bytes, file.FileName, file.ContentType, cancellation: ct);
    }

    /// <summary>The uploader, or an account administering the tenant.</summary>
    private bool CanRead(StoredFile file)
    {
        if (User.IsInRole("SuperAdmin") || User.IsInRole("Admin"))
        {
            return true;
        }

        return Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            && userId != Guid.Empty
            && file.UploadedBy == userId;
    }
}
