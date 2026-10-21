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
/// and entries are applied to it through the config engine as one transaction. The tenant and its
/// administrator are stored only after that applied, so a refused import returns the plan that
/// explains it and leaves no tenant or membership.
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
        var callerId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : (Guid?)null;
        var caller = ConfigCaller.ForUser(User);

        // The tenant and its administrator are stored only after the bundle has applied, so a
        // refused import leaves no tenant and no membership to take back. The plan comes first
        // because it writes nothing and records nothing; the apply that follows can still be
        // refused by a write that lands in between, and that records one config.refused entry.
        ConfigRunResult result;
        using (var scope = services.CreateScopeForTenant(handle))
        {
            var engine = scope.ServiceProvider.GetRequiredService<ConfigEngine>();
            result = await engine.RunAsync(config, caller, apply: false, allowDestructive: false, ct);
            if (Refusal(result) is { } refused)
            {
                await Send.ResponseAsync(new Response { Tenant = TenantResponse.From(tenant), Config = result.Response }, refused, ct);
                return;
            }

            result = await engine.RunAsync(config, caller, apply: true, allowDestructive: false, ct);
        }

        if (!result.Response.Applied)
        {
            await Send.ResponseAsync(
                new Response { Tenant = TenantResponse.From(tenant), Config = result.Response },
                result.StatusCode == 200 ? 409 : result.StatusCode, ct);
            return;
        }

        await StoreAsync(tenant, callerId, result.Response, ct);

        await Send.OkAsync(new Response { Tenant = TenantResponse.From(tenant), Config = result.Response }, ct);
    }

    private async Task StoreAsync(Tenant tenant, Guid? callerId, ConfigPlanResponse applied, CancellationToken ct)
    {
        var handle = tenant.Slug;
        session.Store(tenant);

        // The creator is its administrator, or the cross-tenant token guard denies them the tenant
        // they just made. See CreateTenantEndpoint.
        if (callerId is { } creator)
        {
            await Members.Members.AddAsync(
                session, User, creator, handle, [barakoCMS.Data.DataSeeder.AdminRoleId], profile: null,
                new Dictionary<string, object> { ["invited"] = false, ["tenantCreated"] = true, ["imported"] = true }, ct);
        }

        await AuditLog.RecordAsync(session, handle, "tenant.imported", callerId, User.FindFirst("Username")?.Value,
            metadata: new Dictionary<string, object>
            {
                ["sections"] = string.Join(", ", applied.Sections.Where(s => s.Changes.Count > 0).Select(s => s.Section)),
                ["changes"] = applied.Sections.Sum(s => s.Changes.Count),
            }, ct: ct);
        await session.SaveChangesAsync(ct);
        domains.Invalidate();
    }

    /// <summary>The status a plan that must not be applied is answered with, or null when it can be.</summary>
    private static int? Refusal(ConfigRunResult plan)
    {
        var response = plan.Response;
        if (plan.StatusCode != 200 || response.Errors.Count > 0 || response.HasSectionErrors)
        {
            return plan.StatusCode == 200 ? 400 : plan.StatusCode;
        }

        if (response.HasRefusals)
        {
            return 403;
        }

        return response.HasConflicts || response.HasDestructiveChanges ? 409 : null;
    }

    private static string? BundleName(System.Text.Json.JsonElement bundle) =>
        bundle.TryGetProperty("tenant", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Object
        && t.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
        && !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!.Trim()
            : null;
}
