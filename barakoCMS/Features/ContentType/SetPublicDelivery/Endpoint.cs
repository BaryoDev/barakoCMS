using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetPublicDelivery;

public class Request
{
    public bool Enabled { get; set; }
}

public class Response
{
    public string Name { get; set; } = string.Empty;
    public bool IsPubliclyDeliverable { get; set; }
}

/// <summary>
/// PUT /api/content-types/{name}/public-delivery — turn anonymous public delivery on or off for one
/// content type.
/// </summary>
/// <remarks>
/// This exists because the opt-in would otherwise be a one-way door. Public delivery became opt-in so
/// that modelling members or a ledger as content no longer hands out an anonymous endpoint nobody
/// asked for — but content types have no update endpoint, so on upgrade every existing type stops
/// being delivered with no supported way to turn it back on. A site serving a blog this way would
/// have needed direct database access to recover.
///
/// Deliberately its own endpoint rather than a general content-type update: enabling anonymous access
/// to a whole content type is a decision worth making on purpose, and worth being able to audit,
/// rather than something that rides along inside a larger edit.
/// </remarks>
public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Put("/api/content-types/{name}/public-delivery");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;

        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == name, ct);

        if (def is null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        def.IsPubliclyDeliverable = req.Enabled;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        _session.Store(def);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Name = def.Name,
            IsPubliclyDeliverable = def.IsPubliclyDeliverable,
        }, ct);
    }
}
