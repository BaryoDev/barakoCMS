using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Tenants;

internal sealed record TenantPublicResponse(
    string Handle, string Name, string? LogoUrl, string? About,
    string? Location, string? LocationUrl, string? SocialHandle, string? Email, string? ContactUrl);

/// <summary>GET /api/tenants/{handle}/public: anonymous public profile for a tenant's landing page.</summary>
/// <remarks>
/// The profile is read from the tenant's published <c>site</c> entry, see <see cref="TenantProfiles"/>.
/// The route stays for callers written against it; <c>GET /api/public/site</c> is where the same
/// values, and everything else about the site, are read from.
/// </remarks>
internal class PublicTenantEndpoint(IQuerySession session, IDocumentStore store) : EndpointWithoutRequest<TenantPublicResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/{handle}/public");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var handle = Route<string>("handle")?.ToLowerInvariant();
        var t = await session.Query<Tenant>().FirstOrDefaultAsync(x => x.Slug == handle, ct);
        if (t is not { Status: TenantStatus.Active }) { await Send.NotFoundAsync(ct); return; }
        var profile = await TenantProfiles.ReadAsync(store, t, ct);
        await Send.OkAsync(new TenantPublicResponse(
            t.Slug, t.Name, profile.LogoUrl, profile.About, profile.Location, profile.LocationUrl,
            profile.SocialHandle, profile.Email, profile.ContactUrl), ct);
    }
}

/// <summary>GET /api/tenants: list all tenants (platform admin).</summary>
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

    // The seven profile fields below are bound so TenantWriteRequestValidator can refuse a value
    // sent in one, by name, and so an update can tell an empty string (clear what is stored) from
    // null or absent (leave it). Dropping the properties would have the binder skip them, and a
    // caller that still sets a tenant's About here would get a 200 for a value nothing stored.
    public string? LogoUrl { get; set; }
    public string? About { get; set; }
    public string? Location { get; set; }
    public string? LocationUrl { get; set; }
    public string? SocialHandle { get; set; }
    public string? Email { get; set; }
    public string? ContactUrl { get; set; }

    /// <summary>Whether the tenant is served. Leaving it and <see cref="IsActive"/> out makes it Active, as before.</summary>
    public TenantStatus? Status { get; set; }

    /// <summary>The flag clients sent before <see cref="Status"/>: true is Active, false Suspended.</summary>
    /// <remarks>
    /// False leaves an Archived tenant Archived, so a client that echoes a tenant back with this
    /// flag does not move it between the two.
    /// </remarks>
    public bool? IsActive { get; set; }

    /// <summary>Who may join by signing up. Null on an update leaves the policy, and its role, as they are.</summary>
    public TenantSignUp? SignUp { get; set; }

    /// <summary>The role an Open sign-up gives. Required with Open, refused with anything else.</summary>
    public Guid? SignUpRoleId { get; set; }

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

/// <summary>Refuses a tenant write that sets a profile field, naming the site field it moved to.</summary>
/// <remarks>
/// A blank is not a value, so a client that echoes a tenant back with nothing in these is not
/// refused, and an update can send an empty string to clear one. Covers create and update, which
/// share the request.
/// </remarks>
internal sealed class TenantWriteRequestValidator : Validator<TenantWriteRequest>
{
    public TenantWriteRequestValidator()
    {
        RuleFor(r => r.LogoUrl).Must(BeBlank).WithMessage(Moved("Logo"));
        RuleFor(r => r.About).Must(BeBlank).WithMessage(Moved("About"));
        RuleFor(r => r.Location).Must(BeBlank).WithMessage(Moved("Location"));
        RuleFor(r => r.LocationUrl).Must(BeBlank).WithMessage(Moved("LocationUrl"));
        RuleFor(r => r.SocialHandle).Must(BeBlank).WithMessage(Moved("SocialHandle"));
        RuleFor(r => r.Email).Must(BeBlank).WithMessage(Moved("Email"));
        RuleFor(r => r.ContactUrl).Must(BeBlank).WithMessage(Moved("ContactUrl"));

        RuleFor(r => r.Status).IsInEnum();
        RuleFor(r => r.SignUp).IsInEnum();
        RuleFor(r => r.SignUpRoleId).NotNull().When(r => r.SignUp == TenantSignUp.Open)
            .WithMessage("An Open sign-up policy needs signUpRoleId, the role a person who signs up is given.");
        RuleFor(r => r.SignUpRoleId).Null().When(r => r.SignUp != TenantSignUp.Open)
            .WithMessage("signUpRoleId is only read with signUp set to Open.");
        RuleFor(r => r.IsActive)
            .Must((r, active) => active is null || r.Status is null || active == (r.Status == TenantStatus.Active))
            .WithMessage("isActive disagrees with status. Send status alone.");
    }

    /// <summary>The status a write asks for: Status if sent, else the IsActive flag, else Active.</summary>
    public static TenantStatus Requested(TenantWriteRequest r, TenantStatus current) =>
        r.Status
        ?? (r.IsActive is false
            ? (current == TenantStatus.Active ? TenantStatus.Suspended : current)
            : TenantStatus.Active);

    private static bool BeBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Moved(string siteField) =>
        $"A tenant no longer holds a profile. Set the {siteField} field of its {TenantProfiles.SiteType} entry "
      + "and leave this one out.";
}

/// <summary>The checks a sign-up role needs from the store, which a validator cannot make.</summary>
internal static class TenantSignUpRole
{
    /// <summary>Why <paramref name="roleId"/> cannot be the role an Open sign-up gives, or null.</summary>
    /// <remarks>
    /// Anyone with an email address can sign up to an Open tenant, so the role is held to the rule a
    /// tenant administrator's grant is: never SuperAdmin, and never a role that reaches past the tenant.
    /// </remarks>
    public static async Task<string?> RefusalAsync(IQuerySession session, Guid roleId, CancellationToken ct)
    {
        var role = await session.LoadAsync<Role>(roleId, ct);
        if (role is null)
            return "The sign-up role does not exist.";

        if (!barakoCMS.Features.Tenants.Members.Members.IsAssignable(roleId)
            || barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability(role))
            return "The sign-up role carries platform access, so it cannot be given to whoever signs up.";

        return null;
    }
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
        var domains = TenantDomains.Normalise(req.Domains ?? new List<string>(), out var domainErrors);
        foreach (var error in domainErrors)
        { AddError(r => r.Domains, error); }
        ThrowIfAnyErrors();

        if (await _session.Query<Tenant>().AnyAsync(x => x.Slug == handle, ct))
        { AddError(r => r.Handle, "A tenant with this handle already exists."); ThrowIfAnyErrors(); }

        if (req.SignUpRoleId is { } signUpRole && await TenantSignUpRole.RefusalAsync(_session, signUpRole, ct) is { } refusal)
        { AddError(r => r.SignUpRoleId, refusal); ThrowIfAnyErrors(); }

        if (await TenantDomainClash.FindAsync(_session, domains, exceptSlug: null, ct) is { } clash)
        { ThrowError($"'{clash.Domain}' is already a domain of tenant '{clash.Slug}'. A domain belongs to one tenant.", 409); }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = handle,
            Name = req.Name,
            Status = TenantWriteRequestValidator.Requested(req, TenantStatus.Active),
            SignUp = req.SignUp ?? TenantSignUp.NotSet,
            SignUpRoleId = req.SignUpRoleId,
            Domains = domains.ToList(),
        };
        _session.Store(tenant);

        // Provision the creator as an Active admin member in the same transaction. Without this, a
        // freshly-registered tenant has zero memberships — and the cross-tenant token guard (H.1)
        // then denies login to it for everyone, including its creator. Atomic with the tenant so
        // there is never a registered-but-memberless window.
        if (Guid.TryParse(User.FindFirst("UserId")?.Value, out var creatorId))
        {
            // Through the one writer of a membership, which stages the entry in the new tenant's
            // own log, the one its administrators read.
            await barakoCMS.Features.Tenants.Members.Members.AddAsync(
                _session, User, creatorId, handle,
                new List<Guid> { barakoCMS.Data.DataSeeder.AdminRoleId }, profile: null,
                new Dictionary<string, object> { ["invited"] = false, ["tenantCreated"] = true }, ct);
        }

        await _session.SaveChangesAsync(ct);
        _domains.Invalidate();
        await Send.OkAsync(TenantResponse.From(tenant), ct);
    }
}

/// <summary>PUT /api/tenants/{handle}: update a tenant's name, domains and status (platform admin).</summary>
/// <remarks>
/// A profile value still on the stored document is left as it is unless the request sends that
/// field as an empty string, which blanks it. This used to overwrite all seven with whatever the
/// request held, so a request that left them out blanked them.
/// </remarks>
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

        IReadOnlyList<string>? domains = null;
        if (req.Domains is not null)
        {
            domains = TenantDomains.Normalise(req.Domains, out var domainErrors);
            foreach (var error in domainErrors)
            { AddError(r => r.Domains, error); }
        }
        if (req.SignUpRoleId is { } signUpRole && await TenantSignUpRole.RefusalAsync(_session, signUpRole, ct) is { } refusal)
            AddError(r => r.SignUpRoleId, refusal);
        ThrowIfAnyErrors();

        if (domains is not null
            && await TenantDomainClash.FindAsync(_session, domains, exceptSlug: tenant.Slug, ct) is { } clash)
        { ThrowError($"'{clash.Domain}' is already a domain of tenant '{clash.Slug}'. A domain belongs to one tenant.", 409); }

        tenant.Name = req.Name;
        TenantProfiles.ClearBlanked(tenant, req);
        // Switching a tenant off stops it being served, tokens being issued for it and its API keys,
        // and switching it on restores all three, so any change is recorded in that tenant's log.
        var status = TenantWriteRequestValidator.Requested(req, tenant.Status);
        if (tenant.Status != status)
        {
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
                _session, tenant.Slug, status == TenantStatus.Active ? "tenant.activated" : "tenant.deactivated", actorId,
                User.FindFirst("Username")?.Value, targetType: "Tenant", targetId: tenant.Slug,
                metadata: new Dictionary<string, object> { ["from"] = tenant.Status.ToString(), ["to"] = status.ToString() },
                ct: ct);
        }

        tenant.Status = status;
        if (req.SignUp is { } signUp)
        {
            if (tenant.SignUp != signUp || tenant.SignUpRoleId != req.SignUpRoleId)
            {
                Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
                await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
                    _session, tenant.Slug, "tenant.signup_changed", actorId,
                    User.FindFirst("Username")?.Value, targetType: "Tenant", targetId: tenant.Slug,
                    metadata: new Dictionary<string, object>
                    {
                        ["from"] = tenant.SignUp.ToString(),
                        ["to"] = signUp.ToString(),
                        ["roleId"] = req.SignUpRoleId?.ToString() ?? string.Empty,
                    },
                    ct: ct);
            }

            tenant.SignUp = signUp;
            tenant.SignUpRoleId = req.SignUpRoleId;
        }

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
