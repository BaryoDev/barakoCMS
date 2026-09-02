using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Tenants.Members;

/// <summary>One person's place in the tenant the caller is signed in to.</summary>
internal sealed record MemberResponse(
    Guid UserId,
    string Username,
    string Email,
    List<Guid> RoleIds,
    MembershipStatus Status,
    DateTimeOffset JoinedAt);

/// <summary>A role an administrator of a tenant may hand out inside it.</summary>
internal sealed record AssignableRoleResponse(Guid Id, string Name, string Description);

/// <summary>
/// Shared plumbing for the four member endpoints.
/// </summary>
/// <remarks>
/// Every route here operates on the caller's <em>current</em> tenant rather than one named in the
/// path. <c>TenantAccessMiddleware</c> already refuses a request whose token was minted for a
/// different tenant than the one resolved from the host, and <c>TokenIssuer</c> puts the caller's
/// effective roles for that tenant into the token, so <c>Roles("SuperAdmin", "Admin")</c> reaching
/// a handler already means an administrator of this tenant. A handle in the route would mean
/// re-deriving that in every endpoint, and an administrator of one tenant reaching another is then
/// one forgotten check away.
/// </remarks>
internal static class Members
{
    /// <summary>
    /// SuperAdmin is a platform role, not a tenant one. Granting it through a per-tenant surface
    /// would let an administrator of any tenant mint themselves platform access, which is the one
    /// escalation these routes could offer.
    /// </summary>
    public static bool IsAssignable(Guid roleId) => roleId != SystemRoles.SuperAdminRoleId;

    public static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static MemberResponse ToResponse(Membership membership, User? user) => new(
        membership.UserId,
        user?.Username ?? string.Empty,
        user?.Email ?? string.Empty,
        membership.RoleIds,
        membership.Status,
        Instant(membership.JoinedAt));
}

/// <summary>GET /api/tenants/members: the roster for the caller's tenant, newest first.</summary>
internal sealed class ListMembersEndpoint : Endpoint<ListRequest, PaginatedResponse<MemberResponse>>
{
    private readonly IQuerySession _session;
    private readonly TenantContext _tenant;

    public ListMembersEndpoint(IQuerySession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Get("/api/tenants/members");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var slug = _tenant.Slug;

        // Membership is SingleTenanted (it maps global users to tenants, so it cannot live inside a
        // tenant's partition). The slug filter is therefore the whole isolation guarantee on this
        // query, not a convenience on top of one Marten applies.
        var memberships = await _session.Query<Membership>()
            .Where(m => m.TenantSlug == slug && m.Status != MembershipStatus.Removed)
            .ToListAsync(ct);

        var userIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = (await _session.Query<User>()
                .Where(u => userIds.Contains(u.Id))
                .ToListAsync(ct))
            .ToDictionary(u => u.Id);

        // Paged in memory: the set is already narrowed to one tenant's roster by a join the query
        // cannot express, the same shape /api/me/tenants uses.
        var rows = memberships
            .OrderByDescending(m => m.JoinedAt)
            .Select(m => Members.ToResponse(m, users.GetValueOrDefault(m.UserId)))
            .ToList();

        await Send.OkAsync(rows.ToPagedResponse(req), ct);
    }
}

internal sealed class AddMemberRequest
{
    public string Email { get; set; } = string.Empty;
    public List<Guid> RoleIds { get; set; } = new();
}

/// <summary>
/// POST /api/tenants/members: add a person to the caller's tenant by email.
/// </summary>
internal sealed class AddMemberEndpoint : Endpoint<AddMemberRequest, MemberResponse>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissions;

    public AddMemberEndpoint(
        IDocumentSession session,
        TenantContext tenant,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissions)
    {
        _session = session;
        _tenant = tenant;
        _permissions = permissions;
    }

    public override void Configure()
    {
        Post("/api/tenants/members");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(AddMemberRequest req, CancellationToken ct)
    {
        var slug = _tenant.Slug;
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            AddError(r => r.Email, "A valid email address is required.");

        var roleIds = (req.RoleIds ?? new List<Guid>()).Distinct().ToList();
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            AddError(r => r.RoleIds, "SuperAdmin is a platform role and cannot be granted inside a tenant.");

        ThrowIfAnyErrors();

        if (roleIds.Count > 0)
        {
            var known = await _session.Query<Role>().Where(r => roleIds.Contains(r.Id)).CountAsync(ct);
            if (known != roleIds.Count)
            {
                AddError(r => r.RoleIds, "One or more roles do not exist.");
                ThrowIfAnyErrors();
            }
        }

        var user = await _session.Query<User>().FirstOrDefaultAsync(u => u.Email.ToLower() == email, ct);
        var invited = user is null;

        if (user is null)
        {
            // No password. They sign in with an emailed code, the same account shape social
            // sign-in already produces, which the login path knows how to refuse safely.
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                Username = await AvailableUsernameAsync(email, ct),
                PasswordHash = string.Empty,
                RoleIds = new List<Guid>(),
            };
            _session.Store(user);
        }

        var membership = await _session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantSlug == slug, ct);

        if (membership is null)
        {
            membership = new Membership
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantSlug = slug,
                RoleIds = roleIds,
                Status = MembershipStatus.Active,
                JoinedAt = DateTime.UtcNow,
            };
        }
        else
        {
            // Re-adding somebody who was removed reactivates the row they already have. A second
            // row for the same pair would make EffectiveRoleIdsAsync depend on which one it read
            // first, and would lose the date they originally joined.
            membership.Status = MembershipStatus.Active;
            membership.RoleIds = roleIds;
        }

        _session.Store(membership);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, slug, "tenant.member.added", actorId,
            User.FindFirst("Username")?.Value,
            targetType: "User", targetId: user.Id.ToString(),
            metadata: new() { ["invited"] = invited, ["roleIds"] = roleIds.Select(r => r.ToString()).ToList() },
            ct: ct);

        await _session.SaveChangesAsync(ct);
        _permissions.InvalidateUserPermissions(user.Id);

        await Send.OkAsync(Members.ToResponse(membership, user), ct);
    }

    /// <summary>
    /// Username carries a unique index, so an invited address that happens to match an existing
    /// username would fail the insert with a 500 instead of adding the member.
    /// </summary>
    private async Task<string> AvailableUsernameAsync(string email, CancellationToken ct)
    {
        if (!await _session.Query<User>().AnyAsync(u => u.Username == email, ct))
            return email;

        return $"{email}+{Guid.NewGuid():N}"[..(email.Length + 9)];
    }
}

internal sealed class UpdateMemberRequest
{
    public Guid UserId { get; set; }
    public List<Guid> RoleIds { get; set; } = new();
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
}

/// <summary>
/// PUT /api/tenants/members/{userId}: change a member's roles or status within the caller's tenant.
/// </summary>
internal sealed class UpdateMemberEndpoint : Endpoint<UpdateMemberRequest, MemberResponse>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissions;

    public UpdateMemberEndpoint(
        IDocumentSession session,
        TenantContext tenant,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissions)
    {
        _session = session;
        _tenant = tenant;
        _permissions = permissions;
    }

    public override void Configure()
    {
        Put("/api/tenants/members/{userId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateMemberRequest req, CancellationToken ct)
    {
        var slug = _tenant.Slug;

        var roleIds = (req.RoleIds ?? new List<Guid>()).Distinct().ToList();
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            AddError(r => r.RoleIds, "SuperAdmin is a platform role and cannot be granted inside a tenant.");

        if (req.Status == MembershipStatus.Removed)
            AddError(r => r.Status, "Use DELETE /api/tenants/members/{userId} to remove a member.");

        ThrowIfAnyErrors();

        if (roleIds.Count > 0)
        {
            var known = await _session.Query<Role>().Where(r => roleIds.Contains(r.Id)).CountAsync(ct);
            if (known != roleIds.Count)
            {
                AddError(r => r.RoleIds, "One or more roles do not exist.");
                ThrowIfAnyErrors();
            }
        }

        var membership = await _session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == req.UserId
                                      && m.TenantSlug == slug
                                      && m.Status != MembershipStatus.Removed, ct);
        if (membership is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        membership.RoleIds = roleIds;
        membership.Status = req.Status;
        _session.Store(membership);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, slug, "tenant.member.updated", actorId,
            User.FindFirst("Username")?.Value,
            targetType: "User", targetId: req.UserId.ToString(),
            metadata: new() { ["status"] = req.Status.ToString(), ["roleIds"] = roleIds.Select(r => r.ToString()).ToList() },
            ct: ct);

        await _session.SaveChangesAsync(ct);
        _permissions.InvalidateUserPermissions(req.UserId);

        var user = await _session.LoadAsync<User>(req.UserId, ct);
        await Send.OkAsync(Members.ToResponse(membership, user), ct);
    }
}

internal sealed class RemoveMemberRequest
{
    public Guid UserId { get; set; }
}

internal sealed class RemoveMemberResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// DELETE /api/tenants/members/{userId}: mark a member Removed in the caller's tenant.
/// </summary>
internal sealed class RemoveMemberEndpoint : Endpoint<RemoveMemberRequest, RemoveMemberResponse>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissions;

    public RemoveMemberEndpoint(
        IDocumentSession session,
        TenantContext tenant,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissions)
    {
        _session = session;
        _tenant = tenant;
        _permissions = permissions;
    }

    public override void Configure()
    {
        Delete("/api/tenants/members/{userId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RemoveMemberRequest req, CancellationToken ct)
    {
        var slug = _tenant.Slug;

        var membership = await _session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == req.UserId
                                      && m.TenantSlug == slug
                                      && m.Status != MembershipStatus.Removed, ct);
        if (membership is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Marked, never deleted. The row is what the audit trail and a later re-add both read, and
        // deleting it would silently start somebody's history over.
        membership.Status = MembershipStatus.Removed;
        _session.Store(membership);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, slug, "tenant.member.removed", actorId,
            User.FindFirst("Username")?.Value,
            targetType: "User", targetId: req.UserId.ToString(), ct: ct);

        await _session.SaveChangesAsync(ct);
        _permissions.InvalidateUserPermissions(req.UserId);

        await Send.OkAsync(new RemoveMemberResponse { Message = "Member removed from this tenant." }, ct);
    }
}

/// <summary>
/// GET /api/tenants/members/roles: the roles an administrator may assign inside a tenant.
/// </summary>
internal sealed class AssignableRolesEndpoint : Endpoint<ListRequest, PaginatedResponse<AssignableRoleResponse>>
{
    private readonly IQuerySession _session;

    public AssignableRolesEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/tenants/members/roles");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var roles = await _session.Query<Role>().OrderBy(r => r.Name).ToListAsync(ct);

        // Filtered by the same predicate the write paths refuse on, so the list a client is offered
        // and the list the server accepts cannot drift apart.
        var assignable = roles
            .Where(r => Members.IsAssignable(r.Id))
            .Select(r => new AssignableRoleResponse(r.Id, r.Name, r.Description))
            .ToList();

        await Send.OkAsync(assignable.ToPagedResponse(req), ct);
    }
}
