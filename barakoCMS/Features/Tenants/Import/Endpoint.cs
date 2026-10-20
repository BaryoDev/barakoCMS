using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants.Import;

/// <summary>
/// POST /api/tenants/import: creates a new tenant from a bundle (platform admin).
/// </summary>
/// <remarks>
/// <para>
/// Always a new tenant: a handle that exists answers 409 and nothing is written, so an import can
/// never overwrite or merge into a tenant that is serving. The tenant is created Active with the
/// caller as its administrator, as <c>POST /api/tenants</c> does, and the bundle's configuration
/// and entries are applied to it through the config engine as one transaction. If that is refused
/// the tenant is removed again, the refusal is returned as the plan that explains it, and nothing
/// of the bundle is left.
/// </para>
/// <para>
/// The caller needs the capability each section asks for in the new tenant, exactly as for
/// <c>POST /api/config/apply</c>, so an import cannot write what the caller could not write there.
/// Domains, sign-up and sign-in rules are not in a bundle: set them on the new tenant afterwards.
/// </para>
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(
    IDocumentSession session,
    IServiceProvider services,
    IConfiguration configuration,
    ITenantDomainSource domains) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/tenants/import");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var handle = req.Handle.Trim().ToLowerInvariant();
        var document = TenantBundle.ToConfigDocument(req.Bundle, TenantBundle.MaxImportEntries(configuration), []);
        if (document is not { } config)
        {
            ThrowError("bundle cannot be read.", 400);
            return;
        }

        if (await session.Query<Tenant>().AnyAsync(t => t.Slug == handle, ct))
        {
            ThrowError($"A tenant with the handle '{handle}' already exists. An import only creates a new tenant.", 409);
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = handle,
            Name = string.IsNullOrWhiteSpace(req.Name) ? BundleName(req.Bundle) ?? handle : req.Name.Trim(),
            Status = TenantStatus.Active,
        };
        session.Store(tenant);

        // The creator is its administrator in the same transaction, or the cross-tenant token guard
        // denies them the tenant they just made. See CreateTenantEndpoint.
        var callerId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : (Guid?)null;
        if (callerId is { } creator)
        {
            await Members.Members.AddAsync(
                session, User, creator, handle, [barakoCMS.Data.DataSeeder.AdminRoleId], profile: null,
                new Dictionary<string, object> { ["invited"] = false, ["tenantCreated"] = true, ["imported"] = true }, ct);
        }

        await session.SaveChangesAsync(ct);
        domains.Invalidate();

        ConfigRunResult result;
        using (var scope = services.CreateScopeForTenant(handle))
        {
            result = await scope.ServiceProvider.GetRequiredService<ConfigEngine>()
                .RunAsync(config, ConfigCaller.ForUser(User), apply: true, allowDestructive: false, ct);
        }

        if (!result.Response.Applied)
        {
            session.Delete(tenant);
            session.DeleteWhere<Membership>(m => m.TenantSlug == handle);
            await session.SaveChangesAsync(ct);
            domains.Invalidate();

            await Send.ResponseAsync(
                new Response { Tenant = TenantResponse.From(tenant), Config = result.Response },
                result.StatusCode == 200 ? 409 : result.StatusCode, ct);
            return;
        }

        await AuditLog.RecordAsync(session, handle, "tenant.imported", callerId, User.FindFirst("Username")?.Value,
            metadata: new Dictionary<string, object>
            {
                ["sections"] = string.Join(", ", result.Response.Sections.Where(s => s.Changes.Count > 0).Select(s => s.Section)),
                ["changes"] = result.Response.Sections.Sum(s => s.Changes.Count),
            }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Tenant = TenantResponse.From(tenant), Config = result.Response }, ct);
    }

    private static string? BundleName(System.Text.Json.JsonElement bundle) =>
        bundle.TryGetProperty("tenant", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Object
        && t.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
        && !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!.Trim()
            : null;
}
