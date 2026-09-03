using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Portability;

/// <summary>GET /api/portability/export?types=member,event — download a content bundle (all types if omitted).</summary>
public class ExportEndpoint : Endpoint<ExportEndpoint.Req, PortabilityBundle>
{
    private readonly IQuerySession _session;
    private readonly IDocumentSession _documentSession;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public ExportEndpoint(
        IQuerySession session,
        IDocumentSession documentSession,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _documentSession = documentSession;
        _tenant = tenant;
    }

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

        var types = (await _session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        if (filter != null) types = types.Where(t => filter.Contains(t.Name.ToLowerInvariant())).ToList();

        var contents = await _session.Query<barakoCMS.Models.Content>().ToListAsync(ct);
        if (filter != null) contents = contents.Where(c => filter.Contains(c.ContentType.ToLowerInvariant())).ToList();

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "portability.exported", actorId, User.FindFirst("Username")?.Value,
            metadata: new() { ["contentTypes"] = types.Count, ["contents"] = contents.Count }, ct: ct);
        await _documentSession.SaveChangesAsync(ct);

        await Send.ResponseAsync(new PortabilityBundle
        {
            ContentTypes = types,
            Contents = contents.Select(c => new ContentRecord
            {
                ContentType = c.ContentType,
                Data = c.Data,
                Status = c.Status.ToString(),
            }).ToList(),
        }, cancellation: ct);
    }
}
