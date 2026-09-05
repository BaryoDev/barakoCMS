using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Metadata;

public class Request
{
    public Guid Id { get; set; }
}

/// <summary>
/// GET /api/files/{id}/meta. The record without the bytes, for an editor's file panel.
/// </summary>
public class Endpoint : Endpoint<Request, FileMetadata>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/files/{id}/meta");
        Definition.RequireCapability(FileCapabilities.UploadFiles, FileCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(FileMetadata.From(file), ct);
    }
}
