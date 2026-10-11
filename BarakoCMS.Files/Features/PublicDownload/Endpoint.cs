using barakoCMS.Infrastructure.Caching;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

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
public class Endpoint(IQuerySession session, IFileStorage storage, ImageVariants variants) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/api/public/files/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || !file.IsPublic) { await Send.NotFoundAsync(ct); return; } /* fail closed */

        // A cached resize is reached as ?w= on the file it came from, never by its own id, so this
        // route answers about one record only and it is the one the uploader marked public.
        if (file.ParentFileId is not null) { await Send.NotFoundAsync(ct); return; }

        // After the public check, not before. A resize is the most expensive thing this anonymous
        // route can be made to do, so nothing that costs CPU happens for a file the caller is about
        // to be told does not exist.
        var resolved = await variants.ResolveAsync(file, req.Width, ct);
        if (resolved.Refused is not null)
        {
            AddError(resolved.Refused);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var served = resolved.File;

        // Only a type the upload check would accept goes to the store, which serves it without
        // these headers. Anything else is an old row, streamed from here as a plain download.
        var checkedType = UploadTypes.IsExactly(served.ContentType);

        HttpContext.Response.Headers.CacheControl = "public, max-age=86400"; /* images are long-lived */

        // The bytes of one stored record never change, so the record and how it is served are the
        // version. Hashed, so the id of a resized copy, which is never addressable, is not shown.
        // Answered before the bytes are read, so a revalidation costs no storage read.
        var lastModified = served.CreatedAt.Kind == DateTimeKind.Local
            ? new DateTimeOffset(served.CreatedAt.ToUniversalTime())
            : new DateTimeOffset(DateTime.SpecifyKind(served.CreatedAt, DateTimeKind.Utc));
        var etag = DeliveryCache.WeakETag(
            DeliveryCache.TenantOf(HttpContext),
            $"{served.Id:N}|{served.Size}|{served.ContentType}|{checkedType}|{served.PublicUrl}");
        DeliveryCache.Shared(
            HttpContext, DeliveryCacheClass.Long, [new CacheScope("file", file.Id.ToString("D"))], lastModified);
        HttpContext.Response.Headers.ETag = etag;
        if (DeliveryCache.IsNotModified(HttpContext.Request, etag, lastModified))
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status304NotModified));
            return;
        }

        /* Defense in depth for the proxied bytes: never sniff a different type, and sandbox the
         * response so a document opened directly (a stray SVG/HTML) can't execute script on our origin. */
        HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";

        if (checkedType && !string.IsNullOrEmpty(served.PublicUrl))
        {
            await Send.RedirectAsync(served.PublicUrl, isPermanent: false, allowRemoteRedirects: true);
            return;
        }

        var bytes = await storage.GetAsync(served.StorageKey, ct);
        if (bytes is null)
        {
            // A missing object is not the file, so nothing may keep this 404 for a day under its tag.
            DeliveryCache.Withdraw(HttpContext);
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.BytesAsync(
            bytes, served.FileName, checkedType ? served.ContentType : "application/octet-stream", cancellation: ct);
    }
}
