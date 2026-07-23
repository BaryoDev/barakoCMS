using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants;

public sealed record TenantPublicResponse(
    string Handle, string Name, string? LogoUrl, string? About,
    string? Location, string? LocationUrl, string? SocialHandle, string? Email, string? ContactUrl);

/// <summary>GET /api/tenants/{handle}/public — anonymous public profile for a tenant's landing page.</summary>
public class PublicTenantEndpoint : EndpointWithoutRequest<TenantPublicResponse>
{
    private readonly IQuerySession _session;
    public PublicTenantEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/tenants/{handle}/public");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var t = await _session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle && x.IsActive, ct);
        if (t is null) { await SendNotFoundAsync(ct); return; }
        await SendOkAsync(new TenantPublicResponse(
            t.Slug, t.Name, t.LogoUrl, t.About, t.Location, t.LocationUrl, t.SocialHandle, t.Email, t.ContactUrl), ct);
    }
}

/// <summary>GET /api/tenants — list all tenants with full profile (platform admin).</summary>
public class ListTenantsEndpoint : EndpointWithoutRequest<List<Tenant>>
{
    private readonly IQuerySession _session;
    public ListTenantsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/tenants");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tenants = await _session.Query<Tenant>().ToListAsync(ct);
        await SendOkAsync(tenants.OrderBy(t => t.Name).ToList(), ct);
    }
}

public sealed class TenantWriteRequest
{
    public string Handle { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? About { get; set; }
    public string? Location { get; set; }
    public string? LocationUrl { get; set; }
    public string? SocialHandle { get; set; }
    public string? Email { get; set; }
    public string? ContactUrl { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>POST /api/tenants — create a tenant (platform admin).</summary>
public class CreateTenantEndpoint : Endpoint<TenantWriteRequest, Tenant>
{
    private readonly IDocumentSession _session;
    public CreateTenantEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/tenants");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(TenantWriteRequest req, CancellationToken ct)
    {
        var handle = req.Handle?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!TenantHandles.IsValidHandle(handle))
        { AddError(r => r.Handle, "Invalid or reserved handle (3-40 chars, a-z, 0-9, hyphens)."); }
        if (!string.IsNullOrWhiteSpace(req.ContactUrl) && !TenantHandles.IsValidAbsoluteUrl(req.ContactUrl))
        { AddError(r => r.ContactUrl, "Must be a full http(s) URL."); }
        if (!string.IsNullOrWhiteSpace(req.LocationUrl) && !TenantHandles.IsValidAbsoluteUrl(req.LocationUrl))
        { AddError(r => r.LocationUrl, "Must be a full http(s) URL."); }
        ThrowIfAnyErrors();

        if (await _session.Query<Tenant>().AnyAsync(x => x.Slug == handle, ct))
        { AddError(r => r.Handle, "A tenant with this handle already exists."); ThrowIfAnyErrors(); }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = handle,
            Name = req.Name,
            LogoUrl = req.LogoUrl,
            About = req.About,
            Location = req.Location,
            LocationUrl = req.LocationUrl,
            SocialHandle = req.SocialHandle,
            Email = req.Email,
            ContactUrl = req.ContactUrl,
            IsActive = req.IsActive,
        };
        _session.Store(tenant);

        // Provision the creator as an Active admin member in the same transaction. Without this, a
        // freshly-registered tenant has zero memberships — and the cross-tenant token guard (H.1)
        // then denies login to it for everyone, including its creator. Atomic with the tenant so
        // there is never a registered-but-memberless window.
        if (Guid.TryParse(User.FindFirst("UserId")?.Value, out var creatorId))
        {
            _session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = creatorId,
                TenantSlug = handle,
                RoleIds = new List<Guid> { barakoCMS.Data.DataSeeder.AdminRoleId },
                Status = MembershipStatus.Active,
                JoinedAt = DateTime.UtcNow,
            });
        }

        await _session.SaveChangesAsync(ct);
        await SendOkAsync(tenant, ct);
    }
}

/// <summary>PUT /api/tenants/{handle} — update a tenant's profile (platform admin).</summary>
public class UpdateTenantEndpoint : Endpoint<TenantWriteRequest, Tenant>
{
    private readonly IDocumentSession _session;
    public UpdateTenantEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Put("/api/tenants/{handle}");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(TenantWriteRequest req, CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var tenant = await _session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle, ct);
        if (tenant is null) { await SendNotFoundAsync(ct); return; }

        if (!string.IsNullOrWhiteSpace(req.ContactUrl) && !TenantHandles.IsValidAbsoluteUrl(req.ContactUrl))
        { AddError(r => r.ContactUrl, "Must be a full http(s) URL."); }
        if (!string.IsNullOrWhiteSpace(req.LocationUrl) && !TenantHandles.IsValidAbsoluteUrl(req.LocationUrl))
        { AddError(r => r.LocationUrl, "Must be a full http(s) URL."); }
        ThrowIfAnyErrors();

        tenant.Name = req.Name;
        tenant.LogoUrl = req.LogoUrl;
        tenant.About = req.About;
        tenant.Location = req.Location;
        tenant.LocationUrl = req.LocationUrl;
        tenant.SocialHandle = req.SocialHandle;
        tenant.Email = req.Email;
        tenant.ContactUrl = req.ContactUrl;
        tenant.IsActive = req.IsActive;
        _session.Store(tenant);
        await _session.SaveChangesAsync(ct);
        await SendOkAsync(tenant, ct);
    }
}
