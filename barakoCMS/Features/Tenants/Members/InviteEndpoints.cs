using barakoCMS.Features.Roles;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.Tenants.Members;

internal sealed record InviteRoleResponse(Guid Id, string Name);

internal sealed record InviteRolesResponse(List<InviteRoleResponse> Roles);

internal sealed record InviteResponse(string Status, Guid UserId);

internal sealed class InviteRequest
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Left out, the tenant's one invitable role is granted.</summary>
    public List<Guid>? RoleIds { get; set; }

    public Dictionary<string, string>? Profile { get; set; }
}

internal sealed class InviteValidator : Validator<InviteRequest>
{
    public InviteValidator()
    {
        RuleFor(x => x.Profile).Custom((profile, context) =>
        {
            if (profile is not null && profile.Keys.Any(k => k != Invites.UniqueKey))
            {
                context.AddFailure($"An invite carries only {Invites.UniqueKey}.");
                return;
            }

            if (CallerAttributes.ProfileError(profile) is { } error)
                context.AddFailure(error);
        });
    }
}

internal static class Invites
{
    /// <summary>The one profile key an invite may set, and the one that must be unique among active members.</summary>
    public const string UniqueKey = "memberNo";

    public const string RoleRefused = "That role cannot be granted by an invite.";
    public const string RoleNeeded = "Name the role to grant.";
    public const string Removed = "This person was removed. A manager must add them again.";
    public const string ProfileTaken = "That profile value belongs to another member.";

    /// <summary>The roles of this tenant an invite may grant. SuperAdmin never is, whatever it says.</summary>
    public static async Task<List<Role>> InvitableRolesAsync(IQuerySession session, string slug, CancellationToken ct) =>
        (await RoleScope.In(session.Query<Role>(), slug).Where(r => r.Invitable).ToListAsync(ct))
            .Where(r => Members.IsAssignable(r.Id) && InvitableRule.Allows(r))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

    private const string HoldsValueSql = "lower(btrim(d.data -> 'Profile' ->> ?)) = ?";

    /// <summary>
    /// Whether another active member of the tenant holds this member number. Values compare trimmed and ignoring case. A removed member holds nothing.
    /// </summary>
    public static async Task<bool> ProfileValueTakenAsync(
        IQuerySession session, string slug, Guid? except, Dictionary<string, string>? profile, CancellationToken ct)
    {
        foreach (var (name, value) in (profile ?? new()).Where(p => p.Key == UniqueKey))
        {
            var wanted = value.Trim().ToLowerInvariant();
            var holders = session.Query<Membership>()
                .Where(m => m.TenantSlug == slug && m.Status == MembershipStatus.Active)
                .Where(m => m.MatchesSql(HoldsValueSql, name, wanted));

            if (except is { } me)
                holders = holders.Where(m => m.UserId != me);

            if (await holders.AnyAsync(ct))
                return true;
        }

        return false;
    }
}

/// <summary>GET /api/tenants/members/invite: the roles an invite may grant. A 200 means the caller may invite.</summary>
internal sealed class InviteRolesEndpoint(IQuerySession session, TenantContext tenant) : EndpointWithoutRequest<InviteRolesResponse>
{
    public override void Configure()
    {
        Get("/api/tenants/members/invite");
        Definition.RequireCapability(SystemCapabilities.InviteTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var roles = await Invites.InvitableRolesAsync(session, tenant.Slug, ct);
        await Send.OkAsync(new InviteRolesResponse(roles.Select(r => new InviteRoleResponse(r.Id, r.Name)).ToList()), ct);
    }
}

/// <summary>
/// POST /api/tenants/members/invite: let a person into the caller's tenant with a role marked
/// invitable, and change nothing about anyone who is already here.
/// </summary>
internal sealed class InviteMemberEndpoint(
    IDocumentSession session,
    TenantContext tenant,
    IPermissionResolver permissions) : Endpoint<InviteRequest, InviteResponse>
{
    public override void Configure()
    {
        Post("/api/tenants/members/invite");
        Definition.RequireCapability(SystemCapabilities.InviteTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(InviteRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            AddError(r => r.Email, "A valid email address is required.");

        ThrowIfAnyErrors();

        // Only this tenant's invitable roles are looked at, then intersected with the request, so an
        // id from outside the tenant is never read.
        var invitable = await Invites.InvitableRolesAsync(session, slug, ct);
        var requested = (req.RoleIds ?? []).Distinct().ToList();

        if (requested.Count == 0 && invitable.Count > 1)
            ThrowError(Invites.RoleNeeded, 400);

        var roleIds = requested.Count == 0 ? invitable.Select(r => r.Id).ToList() : requested;
        if (roleIds.Count == 0 || roleIds.Any(id => invitable.All(r => r.Id != id)))
            ThrowError(Invites.RoleRefused, 400);

        var (user, created) = await Members.FindOrCreateUserAsync(session, slug, email, ct);
        if (user is null)
            ThrowError(SignUpPolicy.ClosedToNewAccountsMessage, 403);

        var membership = created
            ? null
            : await session.Query<Membership>().FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantSlug == slug, ct);

        if (membership is { Status: MembershipStatus.Removed })
            ThrowError(Invites.Removed, 409);

        if (membership is not null)
        {
            await Send.OkAsync(new InviteResponse("existing", user.Id), ct);
            return;
        }

        if (await Invites.ProfileValueTakenAsync(session, slug, user.Id, req.Profile, ct))
            ThrowError(Invites.ProfileTaken, 409);

        var details = new Dictionary<string, object> { ["invited"] = created, ["viaInvite"] = true };
        await Members.AddAsync(session, User, user.Id, slug, roleIds, req.Profile, details, ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (Members.IsUniqueViolation(ex))
        {
            ThrowError(AddMemberEndpoint.ConcurrentAdd, 409);
        }

        permissions.InvalidateUserPermissions(user.Id);
        await Send.OkAsync(new InviteResponse("invited", user.Id), ct);
    }
}
