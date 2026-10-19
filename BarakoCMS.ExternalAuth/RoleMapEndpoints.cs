using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.ExternalAuth;

internal sealed record RoleRuleDto(string Provider, string Value, Guid RoleId, string? Issuer = null);

internal sealed record RoleMapRequest(string Claim, List<RoleRuleDto> Rules);

internal sealed record RoleMapResponse(string Claim, List<RoleRuleDto> Rules, DateTime UpdatedAt);

internal static class RoleMaps
{
    public const int MaxRules = 100;
    public const int MaxClaimLength = 100;
    public const int MaxValueLength = 255;

    /// <summary>
    /// Claims a person can edit in their own profile at many providers, or that name the token
    /// itself. A rule on one would let somebody pick their own role.
    /// </summary>
    public static readonly IReadOnlySet<string> RefusedClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "name", "given_name", "family_name", "nickname", "picture", "preferred_username",
        "email", "email_verified", "sub", "iss", "aud",
        "phone_number", "address", "website", "profile", "middle_name", "gender", "birthdate",
        "locale", "zoneinfo", "updated_at",
    };

    /// <summary>Stages the audit entry for a change to the tenant's map, with the rules it held or now holds.</summary>
    public static Task RecordAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string action,
        ExternalRoleMap map, CancellationToken ct)
    {
        Guid.TryParse(actor.FindFirst("UserId")?.Value, out var actorId);
        var metadata = new Dictionary<string, object>
        {
            ["claim"] = map.Claim,
            ["rules"] = map.Rules
                .Select(r => $"{r.Provider}|{r.Value}|{r.RoleId}|{r.Issuer}").ToList(),
        };
        return AuditLog.RecordAsync(session, map.Id, action, actorId, actor.FindFirst("Username")?.Value,
            targetType: "ExternalRoleMap", targetId: map.Id, metadata: metadata, ct: ct);
    }

    public static RoleMapResponse ToResponse(ExternalRoleMap map) =>
        new(map.Claim, map.Rules.Select(r => new RoleRuleDto(r.Provider, r.Value, r.RoleId, r.Issuer)).ToList(), map.UpdatedAt);

    /// <summary>
    /// The roles a person carrying these id token claims is given by the tenant's map. Values match
    /// exactly, since a group name differing only in case is another group, and only for the
    /// provider the rule names: the same group name at another provider is another group. A rule
    /// that names an issuer also needs the token's resolved issuer to be exactly it, since a
    /// provider with a templated issuer signs in people from every directory.
    /// </summary>
    public static List<Guid> RolesFor(
        ExternalRoleMap map, IReadOnlyDictionary<string, IReadOnlyList<string>> claims, string provider,
        string issuer)
    {
        if (RefusedClaims.Contains(map.Claim.Trim()) || !claims.TryGetValue(map.Claim, out var values))
        {
            return [];
        }

        return map.Rules
            .Where(rule => string.Equals(rule.Provider, provider, StringComparison.Ordinal)
                           && (rule.Issuer is null || string.Equals(rule.Issuer, issuer, StringComparison.Ordinal))
                           && values.Contains(rule.Value, StringComparer.Ordinal))
            .Select(rule => rule.RoleId)
            .Distinct()
            .ToList();
    }
}

internal sealed class RoleMapValidator : Validator<RoleMapRequest>
{
    public RoleMapValidator(IConfiguration config)
    {
        RuleFor(x => x.Claim).NotEmpty().MaximumLength(RoleMaps.MaxClaimLength)
            .Must(c => c is null || !RoleMaps.RefusedClaims.Contains(c.Trim()))
            .WithMessage("A rule cannot read a claim the person can edit about themselves, or one that names the token.");
        RuleFor(x => x.Rules).NotNull().Must(r => r is { Count: <= RoleMaps.MaxRules })
            .WithMessage($"A map takes at most {RoleMaps.MaxRules} rules.");
        RuleForEach(x => x.Rules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.Provider).NotEmpty()
                .Must(p => p is not null && OidcProviders.Enabled(config).Any(e => e.Name == p))
                .WithMessage("The provider is not a configured OpenID Connect provider.");
            rule.RuleFor(r => r.Issuer)
                .Must(i => i is null || (OidcProviders.IsIssuerUrl(i) && !i.Contains(OidcProvider.TenantPlaceholder, StringComparison.Ordinal)))
                .WithMessage("The issuer must be the exact issuer URL of the token, with the directory filled in.");
            rule.RuleFor(r => r).Must(r => !TemplatedWithoutIssuer(config, r))
                .WithMessage("This provider signs in people from many directories, so the rule must name the exact issuer.");
            rule.RuleFor(r => r.Value).NotEmpty().MaximumLength(RoleMaps.MaxValueLength)
                .Must(v => v is null || v == v.Trim())
                .WithMessage("A value cannot start or end with a space.");
            rule.RuleFor(r => r.RoleId).NotEmpty();
        });
    }

    private static bool TemplatedWithoutIssuer(IConfiguration config, RoleRuleDto rule) =>
        string.IsNullOrWhiteSpace(rule.Issuer)
        && OidcProviders.Enabled(config).FirstOrDefault(e => e.Name == rule.Provider)?.IssuerIsTemplate == true;
}

/// <summary>GET /api/tenants/external-role-map: the caller's tenant's claim-to-role map.</summary>
internal sealed class GetRoleMapEndpoint(IQuerySession session, TenantContext tenant)
    : EndpointWithoutRequest<RoleMapResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/external-role-map");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var map = await session.LoadAsync<ExternalRoleMap>(tenant.Slug, ct);
        if (map is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(RoleMaps.ToResponse(map), ct);
    }
}

/// <summary>PUT /api/tenants/external-role-map: sets the caller's tenant's map, replacing any.</summary>
internal sealed class PutRoleMapEndpoint(IDocumentSession session, TenantContext tenant)
    : Endpoint<RoleMapRequest, RoleMapResponse>
{
    public override void Configure()
    {
        Put("/api/tenants/external-role-map");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RoleMapRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;
        if (slug == Tenant.DefaultSlug)
        {
            AddError("The default tenant has no claim-to-role map.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var refused = await ExternalRoleGrant.RefusedAsync(session, slug, req.Rules.Select(r => r.RoleId).ToList(), ct);
        if (refused.Count > 0)
        {
            AddError(r => r.Rules, "A rule names a role that does not exist, belongs to another tenant, or carries a platform capability.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var map = new ExternalRoleMap
        {
            Id = slug,
            Claim = req.Claim.Trim(),
            Rules = req.Rules.Select(r => new ExternalRoleRule { Provider = r.Provider, Value = r.Value, RoleId = r.RoleId, Issuer = r.Issuer?.Trim() }).ToList(),
            UpdatedAt = DateTime.UtcNow,
        };
        session.Store(map);
        await RoleMaps.RecordAsync(session, User, "tenant.external_role_map.updated", map, ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(RoleMaps.ToResponse(map), ct);
    }
}

/// <summary>DELETE /api/tenants/external-role-map: removes the map. Roles already given stay.</summary>
internal sealed class DeleteRoleMapEndpoint(IDocumentSession session, TenantContext tenant)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/tenants/external-role-map");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var map = await session.LoadAsync<ExternalRoleMap>(tenant.Slug, ct);
        if (map is not null)
        {
            session.Delete(map);
            await RoleMaps.RecordAsync(session, User, "tenant.external_role_map.removed", map, ct);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
