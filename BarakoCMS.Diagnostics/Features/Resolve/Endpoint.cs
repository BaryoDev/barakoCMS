using FastEndpoints;
using Marten;

namespace BarakoCMS.Diagnostics.Features.Resolve;

public class ResolveRequest
{
    public Guid Id { get; set; }
    /// <summary>True to mark resolved, false to reopen. Defaults to true.</summary>
    public bool Resolved { get; set; } = true;
}

/// <summary>POST /api/client-errors/{id}/resolve — mark an error done (or reopen it).</summary>
public class Endpoint : Endpoint<ResolveRequest>
{
    private readonly IDocumentSession _session;
    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/client-errors/{id}/resolve");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ResolveRequest req, CancellationToken ct)
    {
        var error = await _session.LoadAsync<ClientError>(req.Id, ct);
        if (error is null) { await SendNotFoundAsync(ct); return; }

        error.Resolved = req.Resolved;
        error.ResolvedAt = req.Resolved ? DateTime.UtcNow : null;
        _session.Store(error);
        await _session.SaveChangesAsync(ct);
        await SendOkAsync(ct);
    }
}
