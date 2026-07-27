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
        /* Requires authentication (no AllowAnonymous). Callers fetch with a Bearer token. */
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null) { await SendNotFoundAsync(ct); return; }

        if (!string.IsNullOrEmpty(file.PublicUrl))
        {
            HttpContext.Response.StatusCode = 302;
            HttpContext.Response.Headers.Location = file.PublicUrl;
            return;
        }

        var bytes = await _storage.GetAsync(file.StorageKey, ct);
        if (bytes is null) { await SendNotFoundAsync(ct); return; }

        await SendBytesAsync(bytes, file.FileName, file.ContentType, cancellation: ct);
    }
}
