using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Rebuild;

/// <summary>
/// POST /api/content-types/{name}/rebuild, which discards the read model for an event-sourced type
/// and produces it again from the streams.
/// </summary>
/// <remarks>
/// The operator affordance the event-sourced mode exists for. A document that cannot be discarded
/// and rebuilt from its stream is not event sourced whatever the flag says, so a deployment needs a
/// supported way to do it rather than direct database access.
///
/// Refused for a type whose policy is not event sourced. Its document is the source of truth and its
/// stream is an audit trail, so replaying the stream over the document would be an overwrite dressed
/// as a repair.
///
/// This is an operation with a duration, and the duration grows with the streams. Constraint 5 of
/// the decision record is about exactly this: at some stream count a rebuild stops fitting in a
/// deploy window, and that point arrives without warning.
/// </remarks>
internal sealed class Request
{
    /// <summary>The content type to rebuild, from the route.</summary>
    public string Name { get; set; } = string.Empty;
}

internal sealed class Response
{
    public string Name { get; set; } = string.Empty;

    /// <summary>How many documents were produced again from their streams.</summary>
    public int Rebuilt { get; set; }

    /// <summary>
    /// How many were left alone because somebody wrote to them while the rebuild was running.
    /// </summary>
    /// <remarks>
    /// Not a failure and not a count worth retrying. A write that landed mid-rebuild stored the
    /// current fold itself, so those items are already right; overwriting them with the fold this
    /// rebuild started on would be the regression, not the repair. A non-zero number here just says
    /// the type was being edited at the time.
    /// </remarks>
    public int Skipped { get; set; }
}

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentRebuilder _rebuilder;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        IContentRebuilder rebuilder,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _rebuilder = rebuilder;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/content-types/{name}/rebuild");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(req.Name);

        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == name, ct);

        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var result = await _rebuilder.RebuildAsync(name, ct);

        if (!result.EventSourced)
        {
            AddError(
                $"'{name}' is not event sourced, so its documents are the source of truth and its "
                + "stream is an audit trail. Rebuilding would overwrite them with a replay that "
                + "never saw whatever was written to a document directly.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : Guid.Empty;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "content.rebuilt", actorId,
            User.FindFirst("Username")?.Value ?? string.Empty,
            targetType: name, targetId: name,
            metadata: new() { ["rebuilt"] = result.Rebuilt, ["skipped"] = result.Skipped }, ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Name = name, Rebuilt = result.Rebuilt, Skipped = result.Skipped }, ct);
    }
}
