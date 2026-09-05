using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.PublicMetadata;

public class Request
{
    public Guid Id { get; set; }
}

/// <summary>What a frontend needs to render a public file: the URL it already has, plus the words.</summary>
public class Response
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? PublicUrl { get; set; }
    public string? Alt { get; set; }
    public string? Caption { get; set; }
}

/// <summary>
/// GET /api/public/files/{id}/meta. Anonymous, for a PUBLIC file only, so an <c>&lt;img&gt;</c> can
/// carry the alt text an editor wrote. Anything not public is a 404, indistinguishable from
/// missing, exactly as the bytes next door: metadata is not less private than the file it describes.
/// </summary>
public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/public/files/{id}/meta");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || !file.IsPublic || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Shorter than the bytes' day: an alt text edit should reach the site without a purge.
        HttpContext.Response.Headers.CacheControl = "public, max-age=300";

        await Send.OkAsync(new Response
        {
            Id = file.Id,
            FileName = file.FileName,
            ContentType = file.ContentType,
            Size = file.Size,
            PublicUrl = file.PublicUrl,
            Alt = file.Alt,
            Caption = file.Caption,
        }, ct);
    }
}
