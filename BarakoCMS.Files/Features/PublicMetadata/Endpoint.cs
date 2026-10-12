using barakoCMS.Infrastructure.Caching;
using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Builder;

namespace BarakoCMS.Files.Features.PublicMetadata;

public class Request
{
    public Guid Id { get; set; }

    /// <summary>
    /// A width in pixels, as <c>?w=640</c> on the download. The size, width and height answered are
    /// then those of the copy that download serves, which is a narrower variant or the original. A
    /// width the download refuses is ignored.
    /// </summary>
    [QueryParam, BindFrom("w")]
    public int? Width { get; set; }
}

/// <summary>What a frontend needs to render a public file: the URL it already has, plus the words.</summary>
public class Response
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? PublicUrl { get; set; }

    /// <summary>Width in pixels as shown, so an <c>img</c> can reserve its space. Null when unknown.</summary>
    public int? Width { get; set; }

    /// <summary>Height in pixels as shown. Null whenever <see cref="Width"/> is.</summary>
    public int? Height { get; set; }

    public string? Alt { get; set; }
    public string? Caption { get; set; }
}

/// <summary>
/// GET /api/public/files/{id}/meta. Anonymous, for a PUBLIC file only, so an <c>&lt;img&gt;</c> can
/// carry the alt text an editor wrote. Anything not public is a 404, indistinguishable from
/// missing, exactly as the bytes next door: metadata is not less private than the file it describes.
/// With <c>?w=</c> it describes the copy the download would serve at that width, so a frontend can
/// write a <c>srcset</c> with each variant's own width and height.
/// </summary>
public class Endpoint(IQuerySession session, ImageVariants variants) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/public/files/{id}/meta");
        AllowAnonymous();
        Options(x => x.WithMetadata(DeliveryCache.Validators));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || !file.IsPublic || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // After the public check, as on the download, so nothing costs CPU for a file that is not there.
        // A width the download would refuse is ignored here, as every width was before this route
        // read one, and the answer is the original's.
        var resolved = await variants.ResolveAsync(file, req.Width, ct);
        var served = resolved.Refused is null ? resolved.File : file;

        // Shorter than the bytes' day: an alt text edit should reach the site without a purge.
        HttpContext.Response.Headers.CacheControl = "public, max-age=300";
        DeliveryCache.Shared(HttpContext, DeliveryCacheClass.Short, [new CacheScope("file", file.Id.ToString("D"))]);

        await Send.OkAsync(new Response
        {
            Id = file.Id,
            FileName = file.FileName,
            ContentType = file.ContentType,
            Size = served.Size,
            PublicUrl = file.PublicUrl,
            Width = served.Width,
            Height = served.Height,
            Alt = file.Alt,
            Caption = file.Caption,
        }, ct);
    }
}
