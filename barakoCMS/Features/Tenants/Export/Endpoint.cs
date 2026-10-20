using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants.Export;

/// <summary>
/// GET /api/tenants/{handle}/export: every row that carries the tenant, one JSON object per line.
/// </summary>
/// <remarks>
/// Each line is <c>{"section","scope","document"}</c>, and the last is <c>{"section":"_summary"}</c>
/// with a count per section and the document types withheld. A caller who reads the summary knows
/// the stream ended instead of being cut off. Connector secrets, mail provider settings and pending
/// registrations are not in it. The export is recorded in the tenant's audit log.
/// </remarks>
internal sealed class ExportTenantEndpoint(IQuerySession session, IDocumentSession writer, TenantExportWalker walker)
    : EndpointWithoutRequest
{
    public const string ContentType = "application/x-ndjson";

    public override void Configure()
    {
        Get("/api/tenants/{handle}/export");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle, ct);
        if (tenant is null) { await Send.NotFoundAsync(ct); return; }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(writer, tenant.Slug, "tenant.exported", actorId,
            User.FindFirst("Username")?.Value, targetType: "Tenant", targetId: tenant.Slug, ct: ct);
        await writer.SaveChangesAsync(ct);

        HttpContext.Response.ContentType = ContentType;
        HttpContext.Response.Headers.CacheControl = "no-store";

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        await foreach (var row in walker.WalkAsync(tenant.Slug, ct))
        {
            counts[row.Section] = counts.GetValueOrDefault(row.Section) + 1;
            await WriteLineAsync($"{{\"section\":{JsonSerializer.Serialize(row.Section)},\"scope\":\"{row.Scope}\",\"document\":{row.Json}}}", ct);
        }

        await WriteLineAsync(JsonSerializer.Serialize(new
        {
            section = "_summary",
            tenant = tenant.Slug,
            counts,
            withheld = TenantExportWalker.Withheld.Select(t => t.Name).ToArray(),
        }), ct);
    }

    private Task WriteLineAsync(string line, CancellationToken ct) =>
        HttpContext.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct).AsTask();
}
