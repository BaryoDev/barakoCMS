using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using System.Text.Json;
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
/// and entries are applied to it through the config engine as one transaction. The handle is claimed first
/// as a Suspended tenant, which nothing serves, so two imports of one handle cannot both proceed. It
/// becomes Active, with its administrator, only after the apply committed. A refused import returns
/// the plan that explains it and no tenant, and leaves no tenant or membership.
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
    internal const int NameMaxLength = 200;

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

        var tenant = await ClaimAsync(handle, req, ct);

        // The claim holds the handle in a state nothing serves, so the unique index on the handle
        // settles a race before any data is written. The apply runs in its own transaction (the
        // config engine opens it), so it cannot share a session with the claim. Instead the claim is
        // taken back when the plan, the apply or the activation refuses, throws or is cancelled, and
        // only a committed apply is followed by the activation, which is one small save.
        ConfigRunResult result;
        int? refused;
        try
        {
            (result, refused) = await RunAsync(config, handle, ct);
            if (refused is null)
            {
                await ActivateAsync(tenant, result.Response, ct);
            }
        }
        catch
        {
            await ReleaseAsync(tenant);
            throw;
        }

        if (refused is { } status)
        {
            await ReleaseAsync(tenant);
            await Send.ResponseAsync(new Response { Tenant = null, Config = result.Response }, status, ct);
            return;
        }

        await Send.OkAsync(new Response { Tenant = TenantResponse.From(tenant), Config = result.Response }, ct);
    }

    /// <summary>Stores the tenant row under the handle as Suspended, which nothing serves, or answers 409 when the handle is taken.</summary>
    private async Task<Tenant> ClaimAsync(string handle, Request req, CancellationToken ct)
    {
        var taken = $"A tenant with the handle '{handle}' already exists. An import only creates a new tenant.";
        if (await session.Query<Tenant>().AnyAsync(t => t.Slug == handle, ct))
        {
            ThrowError(taken, 409);
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = handle,
            Name = string.IsNullOrWhiteSpace(req.Name) ? BundleName(req.Bundle) ?? handle : req.Name.Trim(),
            Status = TenantStatus.Suspended,
        };
        session.Store(tenant);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            session.EjectAllPendingChanges();
            ThrowError(taken, 409);
        }

        domains.Invalidate();
        return tenant;
    }

    /// <summary>Plans, then applies, the bundle in the claimed tenant. The status is set when the import must stop.</summary>
    private async Task<(ConfigRunResult Result, int? Refused)> RunAsync(JsonElement config, string handle, CancellationToken ct)
    {
        var caller = ConfigCaller.ForUser(User);
        using var scope = services.CreateScopeForTenant(handle);
        var engine = scope.ServiceProvider.GetRequiredService<ConfigEngine>();

        // The plan writes nothing and records nothing. The apply that follows can still be refused
        // by a write that lands in between, and that records one config.refused entry.
        var plan = await engine.RunAsync(config, caller, apply: false, allowDestructive: false, ct);
        if (Refusal(plan) is { } refused)
        {
            return (plan, refused);
        }

        var applied = await engine.RunAsync(config, caller, apply: true, allowDestructive: false, ct);
        return (applied, applied.Response.Applied ? null : applied.StatusCode == 200 ? 409 : applied.StatusCode);
    }

    /// <summary>
    /// Takes the claim back, even when the request was cancelled, so the handle can be imported again
    /// (no route deletes a tenant). Only the tenant row goes: audit entries the engine recorded under
    /// the handle stay, and so does anything an apply committed before the activation failed.
    /// </summary>
    private async Task ReleaseAsync(Tenant tenant)
    {
        session.EjectAllPendingChanges();
        session.Delete(tenant);
        await session.SaveChangesAsync(CancellationToken.None);
        domains.Invalidate();
    }

    private async Task ActivateAsync(Tenant tenant, ConfigPlanResponse applied, CancellationToken ct)
    {
        var handle = tenant.Slug;
        var callerId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : (Guid?)null;
        tenant.Status = TenantStatus.Active;
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

    /// <summary>The unique index Marten builds on <c>Tenant.Slug</c> (see the schema in ServiceCollectionExtensions).</summary>
    private const string SlugIndex = "mt_doc_tenants_uidx_slug";

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505", ConstraintName: SlugIndex })
            {
                return true;
            }
        }

        return false;
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
            ? Cap(n.GetString()!.Trim())
            : null;

    private static string Cap(string name) => name.Length > NameMaxLength ? name[..NameMaxLength] : name;
}
