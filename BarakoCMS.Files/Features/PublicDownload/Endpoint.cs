using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.PublicDownload;

public class Request
{
    public Guid Id { get; set; }
}

/// <summary>
/// GET /api/public/files/{id} — anonymous read of a PUBLIC file for a website frontend. Anything not
/// marked public returns 404 (fail closed; indistinguishable from missing, so private ids can't be
/// probed). For an object store it redirects to the object's public URL; for Postgres it proxies the
/// bytes. The literal "files" segment wins over the /api/public/{type}/{slug} content route.
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
        Get("/api/public/files/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || !file.IsPublic) { await SendNotFoundAsync(ct); return; } /* fail closed */

        HttpContext.Response.Headers.CacheControl = "public, max-age=86400"; /* images are long-lived */

        /* Defense in depth for the proxied bytes: never sniff a different type, and sandbox the
         * response so a document opened directly (a stray SVG/HTML) can't execute script on our origin. */
        HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

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
