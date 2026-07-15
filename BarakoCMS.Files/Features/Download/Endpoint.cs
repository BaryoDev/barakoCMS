using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Download;

public class Request
{
    public Guid Id { get; set; }
}

/// <summary>GET /api/files/{id} — stream a stored file back with its original content type.</summary>
public class Endpoint : Endpoint<Request>
{
    private readonly IQuerySession _session;
    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/files/{id}");
        // Requires authentication (no AllowAnonymous). Callers fetch with a Bearer token.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendBytesAsync(file.Data, file.FileName, file.ContentType, cancellation: ct);
    }
}
