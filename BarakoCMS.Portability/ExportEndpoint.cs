using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

// The bundle types are obsolete for callers outside the module, and this is the module.
#pragma warning disable CS0618

namespace BarakoCMS.Portability;

/// <summary>GET /api/portability/export?types=member,event — download a content bundle (all types if omitted).</summary>
[Obsolete(PortabilityObsolete.Message)]
public class ExportEndpoint(
    IQuerySession session,
    IDocumentSession documentSession,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<ExportEndpoint.Req, PortabilityBundle>
{
    public class Req { public string? Types { get; set; } }

    public override void Configure()
    {
        Get("/api/portability/export");
        Definition.RequireCapability(
            PortabilityCapabilities.ExportContent, PortabilityCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Req req, CancellationToken ct)
    {
        // The same identity check as the content List endpoint.
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var callerId)
            || await session.LoadAsync<barakoCMS.Models.User>(callerId, ct) is not { } caller)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var filter = string.IsNullOrWhiteSpace(req.Types)
            ? null
            : req.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(s => s.ToLowerInvariant()).ToHashSet();

        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        if (filter != null) types = types.Where(t => filter.Contains(t.Name.ToLowerInvariant())).ToList();

        var contents = await session.Query<barakoCMS.Models.Content>().ToListAsync(ct);
        if (filter != null) contents = contents.Where(c => filter.Contains(c.ContentType.ToLowerInvariant())).ToList();

        var (records, withheld) = await BundleEntries.ExportAsync(
            contents, types, caller, HttpContext,
            Resolve<barakoCMS.Infrastructure.Services.IPermissionResolver>(), Resolve<ISensitivityService>(), ct);

        await AuditLog.RecordAsync(documentSession, tenant.Slug, "portability.exported", callerId, User.FindFirst("Username")?.Value,
            metadata: new()
            {
                ["contentTypes"] = types.Count,
                ["contents"] = contents.Count,
                ["exported"] = records.Count,
                ["withheld"] = withheld,
            }, ct: ct);
        await documentSession.SaveChangesAsync(ct);

        // A stored field lists roles by id, and an id means nothing to the instance a bundle is
        // imported into. The bundle carries names, which the import matches against its own roles.
        await barakoCMS.Core.RoleReferences.ToNamesAsync(session, types.SelectMany(t => t.Fields ?? []), ct);

        await Send.ResponseAsync(new PortabilityBundle
        {
            ContentTypes = types,
            Contents = records,
            ContentsWithheld = withheld,
        }, cancellation: ct);
    }
}
