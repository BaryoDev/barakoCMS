using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants;

internal sealed record TenantPublicResponse(
    string Handle, string Name, string? LogoUrl, string? About,
    string? Location, string? LocationUrl, string? SocialHandle, string? Email, string? ContactUrl);

/// <summary>GET /api/tenants/{handle}/public — anonymous public profile for a tenant's landing page.</summary>
internal class PublicTenantEndpoint(IQuerySession session) : EndpointWithoutRequest<TenantPublicResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/{handle}/public");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var t = await session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle && x.IsActive, ct);
        if (t is null) { await Send.NotFoundAsync(ct); return; }
        await Send.OkAsync(new TenantPublicResponse(
            t.Slug, t.Name, t.LogoUrl, t.About, t.Location, t.LocationUrl, t.SocialHandle, t.Email, t.ContactUrl), ct);
    }
}

/// <summary>GET /api/tenants — list all tenants with full profile (platform admin).</summary>
internal class ListTenantsEndpoint(IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<TenantResponse>>
{
    public override void Configure()
    {
        Get("/api/tenants");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<Tenant>()
            .OrderBy(t => t.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<TenantResponse>
        {
            Items = page.Items.Select(TenantResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

internal sealed class TenantWriteRequest
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

    /// <summary>
    /// The domains the tenant answers on, as bare hosts such as <c>example.com</c>.
    /// </summary>
    /// <remarks>
    /// Null on an update leaves the stored domains as they are, and an empty list clears them. The
    /// distinction is what lets a client that predates this field keep saving a tenant without
    /// wiping its domains.
    /// </remarks>
    public List<string>? Domains { get; set; }
}

/// <summary>Finds a domain already held by another tenant.</summary>
/// <remarks>
/// Inactive tenants count. The map skips them, but a domain given away while its tenant is paused
/// would collide the moment that tenant is switched back on, and the map answers a collision by
/// resolving no custom domain at all.
///
/// A read before the write, so two saves committing at the same instant are not covered. The map
/// logs that case and degrades rather than routing a host to the wrong tenant.
/// </remarks>
internal static class TenantDomainClash
{
    public static async Task<(string Domain, string Slug)?> FindAsync(
        IQuerySession session, IReadOnlyCollection<string> domains, string? exceptSlug, CancellationToken ct)
    {
        if (domains.Count == 0)
            return null;

        var tenants = await session.Query<Tenant>().ToListAsync(ct);
        foreach (var other in tenants)
        {
            if (exceptSlug is not null && string.Equals(other.Slug, exceptSlug, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var stored in other.Domains)
            {
                var held = TenantDomainMap.Normalise(stored);
                if (held is not null && domains.Contains(held))
                    return (held, other.Slug);
            }
        }

        return null;
    }
}

/// <summary>POST /api/tenants — create a tenant (platform admin).</summary>
internal class CreateTenantEndpoint : Endpoint<TenantWriteRequest, TenantResponse>
{
    private readonly IDocumentSession _session;
    private readonly ITenantDomainSource _domains;

    public CreateTenantEndpoint(IDocumentSession session, ITenantDomainSource domains)
    {
        _session = session;
        _domains = domains;
    }

    public override void Configure()
    {
        Post("/api/tenants");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
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
        var domains = TenantDomains.Normalise(req.Domains ?? new List<string>(), out var domainErrors);
        foreach (var error in domainErrors)
        { AddError(r => r.Domains, error); }
        ThrowIfAnyErrors();

        if (await _session.Query<Tenant>().AnyAsync(x => x.Slug == handle, ct))
        { AddError(r => r.Handle, "A tenant with this handle already exists."); ThrowIfAnyErrors(); }

        if (await TenantDomainClash.FindAsync(_session, domains, exceptSlug: null, ct) is { } clash)
        { ThrowError($"'{clash.Domain}' is already a domain of tenant '{clash.Slug}'. A domain belongs to one tenant.", 409); }

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
            Domains = domains.ToList(),
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
        _domains.Invalidate();
        await Send.OkAsync(TenantResponse.From(tenant), ct);
    }
}

/// <summary>PUT /api/tenants/{handle} — update a tenant's profile (platform admin).</summary>
internal class UpdateTenantEndpoint : Endpoint<TenantWriteRequest, TenantResponse>
{
    private readonly IDocumentSession _session;
    private readonly ITenantDomainSource _domains;

    public UpdateTenantEndpoint(IDocumentSession session, ITenantDomainSource domains)
    {
        _session = session;
        _domains = domains;
    }

    public override void Configure()
    {
        Put("/api/tenants/{handle}");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
    }

    public override async Task HandleAsync(TenantWriteRequest req, CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var tenant = await _session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle, ct);
        if (tenant is null) { await Send.NotFoundAsync(ct); return; }

        if (!string.IsNullOrWhiteSpace(req.ContactUrl) && !TenantHandles.IsValidAbsoluteUrl(req.ContactUrl))
        { AddError(r => r.ContactUrl, "Must be a full http(s) URL."); }
        if (!string.IsNullOrWhiteSpace(req.LocationUrl) && !TenantHandles.IsValidAbsoluteUrl(req.LocationUrl))
        { AddError(r => r.LocationUrl, "Must be a full http(s) URL."); }
        IReadOnlyList<string>? domains = null;
        if (req.Domains is not null)
        {
            domains = TenantDomains.Normalise(req.Domains, out var domainErrors);
            foreach (var error in domainErrors)
            { AddError(r => r.Domains, error); }
        }
        ThrowIfAnyErrors();

        if (domains is not null
            && await TenantDomainClash.FindAsync(_session, domains, exceptSlug: tenant.Slug, ct) is { } clash)
        { ThrowError($"'{clash.Domain}' is already a domain of tenant '{clash.Slug}'. A domain belongs to one tenant.", 409); }

        tenant.Name = req.Name;
        tenant.LogoUrl = req.LogoUrl;
        tenant.About = req.About;
        tenant.Location = req.Location;
        tenant.LocationUrl = req.LocationUrl;
        tenant.SocialHandle = req.SocialHandle;
        tenant.Email = req.Email;
        tenant.ContactUrl = req.ContactUrl;
        tenant.IsActive = req.IsActive;
        if (domains is not null)
            tenant.Domains = domains.ToList();
        _session.Store(tenant);
        await _session.SaveChangesAsync(ct);

        // Every update, not only a change of domains: switching a tenant off takes its domains out of
        // the map too, and waiting out the cache would keep routing to it for minutes.
        _domains.Invalidate();
        await Send.OkAsync(TenantResponse.From(tenant), ct);
    }
}
