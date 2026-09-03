using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.PublicDownload;

public class Request
{
    public Guid Id { get; set; }

    /// <summary>Requested width in pixels, as <c>?w=400</c>. Absent means the file unchanged.</summary>
    [QueryParam, BindFrom("w")]
    public int? Width { get; set; }
}

/// <summary>
/// GET /api/public/files/{id} — anonymous read of a PUBLIC file for a website frontend. Anything not
/// marked public returns 404 (fail closed; indistinguishable from missing, so private ids can't be
/// probed). For an object store it redirects to the object's public URL; for Postgres it proxies the
/// bytes. The literal "files" segment wins over the /api/public/{type}/{slug} content route.
/// Add <c>?w=400</c> for a narrower copy of an image; see <c>docs/image-variants.md</c>.
/// </summary>
public class Endpoint : Endpoint<Request>
{
    private readonly IQuerySession _session;
    private readonly IFileStorage _storage;
    private readonly ImageVariants _variants;

    public Endpoint(IQuerySession session, IFileStorage storage, ImageVariants variants)
    {
        _session = session;
        _storage = storage;
        _variants = variants;
    }

    public override void Configure()
    {
        Get("/api/public/files/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || !file.IsPublic) { await Send.NotFoundAsync(ct); return; } /* fail closed */

        // A cached resize is reached as ?w= on the file it came from, never by its own id, so this
        // route answers about one record only and it is the one the uploader marked public.
        if (file.ParentFileId is not null) { await Send.NotFoundAsync(ct); return; }

        // After the public check, not before. A resize is the most expensive thing this anonymous
        // route can be made to do, so nothing that costs CPU happens for a file the caller is about
        // to be told does not exist.
        var resolved = await _variants.ResolveAsync(file, req.Width, ct);
        if (resolved.Refused is not null)
        {
            AddError(resolved.Refused);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var served = resolved.File;

        HttpContext.Response.Headers.CacheControl = "public, max-age=86400"; /* images are long-lived */

        /* Defense in depth for the proxied bytes: never sniff a different type, and sandbox the
         * response so a document opened directly (a stray SVG/HTML) can't execute script on our origin. */
        HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

        if (!string.IsNullOrEmpty(served.PublicUrl))
        {
            HttpContext.Response.StatusCode = 302;
            HttpContext.Response.Headers.Location = served.PublicUrl;
            return;
        }

        var bytes = await _storage.GetAsync(served.StorageKey, ct);
        if (bytes is null) { await Send.NotFoundAsync(ct); return; }

        await Send.BytesAsync(bytes, served.FileName, served.ContentType, cancellation: ct);
    }
}
