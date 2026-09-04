using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.List;

public class Request : PaginatedRequest
{
    /// <summary>A case-insensitive substring of the file name, for a picker's search box.</summary>
    public string? Q { get; set; }

    /// <summary>A content type, or a prefix of one such as <c>image/</c>.</summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// GET /api/files. The uploads in this tenant, newest first, without the cached resizes: a variant
/// is reached as <c>?w=</c> on its original and has no row of its own here, as on the downloads.
/// </summary>
public class Endpoint : Endpoint<Request, PaginatedResponse<FileMetadata>>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/files");
        Definition.RequireCapability(FileCapabilities.UploadFiles, FileCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = _session.Query<StoredFile>().Where(f => f.ParentFileId == null);

        var name = req.Q?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            query = query.Where(f => f.FileName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        var type = req.ContentType?.Trim();
        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(f => f.ContentType.StartsWith(type, StringComparison.OrdinalIgnoreCase));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await Send.OkAsync(new PaginatedResponse<FileMetadata>
        {
            Items = page.Select(FileMetadata.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, ct);
    }
}
