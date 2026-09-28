using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Portability;

/// <summary>GET /api/portability/export?types=member,event — download a content bundle (all types if omitted).</summary>
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
            PortabilityCapabilities.ExportContent, PortabilityCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Req req, CancellationToken ct)
    {
        var filter = string.IsNullOrWhiteSpace(req.Types)
            ? null
            : req.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(s => s.ToLowerInvariant()).ToHashSet();

        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        if (filter != null) types = types.Where(t => filter.Contains(t.Name.ToLowerInvariant())).ToList();

        var contents = await session.Query<barakoCMS.Models.Content>().ToListAsync(ct);
        if (filter != null) contents = contents.Where(c => filter.Contains(c.ContentType.ToLowerInvariant())).ToList();

        var records = new List<ContentRecord>();
        var withheld = 0;
        var sensitivity = Resolve<ISensitivityService>();
        foreach (var c in contents)
        {
            var data = new Dictionary<string, object>(c.Data);
            var hidden = await sensitivity.ApplyAsync(c.ContentType, c.Sensitivity, data, HttpContext, ct);

            // The read endpoints show an entry the caller may read nothing of as empty or HIDDEN. An
            // import would turn that into a real, empty entry, so it is left out and counted instead.
            if (hidden || (data.Count == 0 && c.Data.Count > 0))
            {
                withheld++;
                continue;
            }

            records.Add(new ContentRecord
            {
                ContentType = c.ContentType,
                Data = data,
                Status = c.Status.ToString(),
                MaskedFields = data
                    .Where(kv => !ReferenceEquals(kv.Value, c.Data[kv.Key]))
                    .Select(kv => kv.Key)
                    .ToList(),
            });
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(documentSession, tenant.Slug, "portability.exported", actorId, User.FindFirst("Username")?.Value,
            metadata: new() { ["contentTypes"] = types.Count, ["contents"] = records.Count, ["contentsWithheld"] = withheld }, ct: ct);
        await documentSession.SaveChangesAsync(ct);

        await Send.ResponseAsync(new PortabilityBundle
        {
            ContentTypes = types,
            Contents = records,
            ContentsWithheld = withheld,
        }, cancellation: ct);
    }
}
