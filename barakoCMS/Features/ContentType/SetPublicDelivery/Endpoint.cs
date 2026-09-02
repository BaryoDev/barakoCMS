using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.ContentType.SetPublicDelivery;

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
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly IConfiguration _configuration;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        IConfiguration configuration)
    {
        _session = session;
        _openApiCache = openApiCache;
        _tenant = tenant;
        _configuration = configuration;
    }

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
            await Send.NotFoundAsync(ct);
            return;
        }

        // Published entries, not all of them. A draft is not served to anonymous callers whatever
        // this setting says, so counting every entry would overstate the exposure and the number
        // would stop meaning anything.
        var published = await _session.Query<ContentDoc>()
            .CountAsync(c => c.ContentType == def.Name && c.Status == ContentStatus.Published, ct);

        if (req.Enabled
            && !def.IsPubliclyDeliverable
            && !req.AcknowledgeExposure
            && _configuration.GetValue("PublicDelivery:RequireAcknowledgement", false))
        {
            AddError(
                $"Turning public delivery on for '{def.Name}' serves {published} published "
                + $"{(published == 1 ? "entry" : "entries")} to anonymous callers, and every entry "
                + "published afterwards. Resend with acknowledgeExposure set to true.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Nothing to record when nothing changed. An audit trail that logs a repeated request as a
        // change makes the entries that were changes harder to find, which is the opposite of what
        // it is for.
        var changed = def.IsPubliclyDeliverable != req.Enabled;

        def.IsPubliclyDeliverable = req.Enabled;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        _session.Store(def);

        if (changed)
        {
            // Two actions rather than one with a direction field, so enabling can be alerted on
            // without the alert also firing every time somebody turns delivery off. The direction is
            // in the metadata as well, for anyone reading the trail rather than matching on it.
            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;

            await AuditLog.RecordAsync(
                _session,
                _tenant.Slug,
                req.Enabled ? "contenttype.publicdelivery.enabled" : "contenttype.publicdelivery.disabled",
                actorId,
                User.FindFirst("Username")?.Value,
                targetType: "ContentType",
                targetId: def.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = def.Name,
                    ["enabled"] = req.Enabled,
                    // The count is what makes the entry useful rather than merely present. "Public
                    // delivery enabled" and "public delivery enabled, 4,000 entries now anonymous"
                    // are different sentences to whoever reads this in six months.
                    ["publishedEntries"] = published,
                },
                ct: ct);
        }

        await _session.SaveChangesAsync(ct);

        // The OpenAPI document lists the deliverable types, so turning one on or off changes it.
        _openApiCache.Invalidate(_tenant.Slug);

        await Send.OkAsync(new Response
        {
            Name = def.Name,
            IsPubliclyDeliverable = def.IsPubliclyDeliverable,
            PublishedEntries = published,
        }, ct);
    }
}
