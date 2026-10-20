using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants.Export;

/// <summary>
/// GET /api/tenants/{handle}/export: one tenant's configuration and entries as a single bundle
/// (platform admin).
/// </summary>
/// <remarks>
/// <para>
/// The configuration comes from the config engine, in the tenant named by the route, for the
/// caller. A section the caller does not hold the read capability for is named under
/// <c>config.withheld</c> and <c>complete</c> is false, so a bundle cut short says so. The entries
/// are read a page at a time and written to the response as they are read, so the size of a tenant
/// is not held in memory by the request.
/// </para>
/// <para>
/// Not in the bundle: members and their accounts (password hashes included), API keys, mail
/// settings, stored files, redirects and the audit log. See <see cref="TenantBundle"/>.
/// </para>
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    IDocumentSession writes,
    IServiceProvider services) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/api/tenants/{handle}/export");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var handle = req.Handle.Trim().ToLowerInvariant();
        if (await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == handle, ct) is not { } tenant)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var callerId)
            || await session.LoadAsync<User>(callerId, ct) is not { } caller)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        using var scope = services.CreateScopeForTenant(tenant.Slug);
        var config = await scope.ServiceProvider.GetRequiredService<ConfigEngine>()
            .ExportAsync(ConfigCaller.ForUser(User), null, ct);

        HttpContext.Response.ContentType = "application/json";
        HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{tenant.Slug}.tenant-bundle.json\"";
        await HttpContext.Response.StartAsync(ct);

        var writer = new Utf8JsonWriter(HttpContext.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("kind", TenantBundle.Kind);
        writer.WriteNumber("bundleVersion", TenantBundle.Version);
        writer.WriteString("exportedAt", DateTime.UtcNow);
        writer.WriteStartObject("tenant");
        writer.WriteString("handle", tenant.Slug);
        writer.WriteString("name", tenant.Name);
        writer.WriteEndObject();
        writer.WritePropertyName("config");
        JsonSerializer.Serialize(writer, config, ConfigJson.Options);
        writer.WriteStartArray("contents");
        await writer.FlushAsync(ct);

        var (written, withheld) = await TenantBundle.WriteEntriesAsync(
            scope.ServiceProvider, writer, () => writer.FlushAsync(ct), caller, HttpContext, ct);

        writer.WriteEndArray();
        writer.WriteNumber("contentsWithheld", withheld);
        writer.WriteBoolean("complete", withheld == 0 && config.Withheld.Count == 0);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);

        await AuditLog.RecordAsync(writes, tenant.Slug, "tenant.exported", callerId, User.FindFirst("Username")?.Value,
            metadata: new Dictionary<string, object>
            {
                ["sections"] = string.Join(", ", config.Sections.Keys),
                ["withheldSections"] = string.Join(", ", config.Withheld),
                ["contents"] = written,
                ["contentsWithheld"] = withheld,
            }, ct: ct);
        await writes.SaveChangesAsync(ct);
    }
}
