using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.ExternalAuth;

internal sealed record RoleRuleDto(string Value, Guid RoleId);

internal sealed record RoleMapRequest(string Claim, List<RoleRuleDto> Rules);

internal sealed record RoleMapResponse(string Claim, List<RoleRuleDto> Rules, DateTime UpdatedAt);

internal static class RoleMaps
{
    public const int MaxRules = 100;
    public const int MaxClaimLength = 100;
    public const int MaxValueLength = 255;

    public static RoleMapResponse ToResponse(ExternalRoleMap map) =>
        new(map.Claim, map.Rules.Select(r => new RoleRuleDto(r.Value, r.RoleId)).ToList(), map.UpdatedAt);

    /// <summary>
    /// The roles a person carrying these id token claims is given by the tenant's map. Values match
    /// exactly, since a group name differing only in case is another group.
    /// </summary>
    public static List<Guid> RolesFor(ExternalRoleMap map, IReadOnlyDictionary<string, IReadOnlyList<string>> claims)
    {
        if (!claims.TryGetValue(map.Claim, out var values))
        {
            return [];
        }

        return map.Rules
            .Where(rule => values.Contains(rule.Value, StringComparer.Ordinal))
            .Select(rule => rule.RoleId)
            .Distinct()
            .ToList();
    }
}

internal sealed class RoleMapValidator : Validator<RoleMapRequest>
{
    public RoleMapValidator()
    {
        RuleFor(x => x.Claim).NotEmpty().MaximumLength(RoleMaps.MaxClaimLength);
        RuleFor(x => x.Rules).NotNull().Must(r => r is { Count: <= RoleMaps.MaxRules })
            .WithMessage($"A map takes at most {RoleMaps.MaxRules} rules.");
        RuleForEach(x => x.Rules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.Value).NotEmpty().MaximumLength(RoleMaps.MaxValueLength);
            rule.RuleFor(r => r.RoleId).NotEmpty();
        });
    }
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
            Rules = req.Rules.Select(r => new ExternalRoleRule { Value = r.Value, RoleId = r.RoleId }).ToList(),
            UpdatedAt = DateTime.UtcNow,
        };
        session.Store(map);
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
        session.Delete<ExternalRoleMap>(tenant.Slug);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
