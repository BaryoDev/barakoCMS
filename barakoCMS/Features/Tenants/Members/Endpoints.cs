using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;
using Marten.Patching;

namespace barakoCMS.Features.Tenants.Members;

/// <summary>One person's place in the tenant the caller is signed in to.</summary>
internal sealed record MemberResponse(
    Guid UserId,
    string Username,
    string Email,
    List<Guid> RoleIds,
    MembershipStatus Status,
    DateTimeOffset JoinedAt,
    Dictionary<string, string> Profile);

/// <summary>A role an administrator of a tenant may hand out inside it.</summary>
internal sealed record AssignableRoleResponse(Guid Id, string Name, string Description);

/// <summary>
/// Shared plumbing for the four member endpoints.
/// </summary>
/// <remarks>
/// Every route here operates on the caller's <em>current</em> tenant rather than one named in the
/// path. <c>TenantAccessMiddleware</c> already refuses a request whose token was minted for a
/// different tenant than the one resolved from the host, and <c>TokenIssuer</c> puts the caller's
/// effective roles for that tenant into the token, so a caller with <c>manage_tenant_members</c> reaching
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

    /// <summary>
    /// A role carrying a platform capability reaches past this tenant (manage_roles edits every
    /// role document), so only a SuperAdmin hands one out here, the same rule the global surface
    /// applies. Only roles being added count: one a SuperAdmin already gave the member is kept on
    /// an unrelated edit, and taking it away is allowed, since removing the member outright is.
    /// </summary>
    public static async Task<bool> RefusesPlatformRolesAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal caller, List<Guid> requested,
        Membership? existing, CancellationToken ct)
    {
        var held = existing is { Status: not MembershipStatus.Removed } ? existing.RoleIds : [];
        var roleIds = requested.Except(held).ToList();
        if (roleIds.Count == 0) return false;

        var roles = await session.Query<Role>().Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        return roles.Any(barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability)
               && !await barakoCMS.Features.Users.PlatformRoles.IsSuperAdminAsync(session, caller, ct);
    }

    /// <summary>What a member holds at one moment, read before a change so the entry can say what it replaced.</summary>
    public sealed record Held(MembershipStatus Status, List<Guid> RoleIds)
    {
        public static Held By(Membership membership) => new(membership.Status, (membership.RoleIds ?? []).ToList());
    }

    /// <summary>
    /// The role part of a member row: ids with names beside them, for the roles being given and,
    /// when the membership already existed, for the status and roles it held before.
    /// </summary>
    public static async Task<Dictionary<string, object>> AuditMetadataAsync(
        IQuerySession session, IReadOnlyCollection<Guid>? roleIds, Held? before, CancellationToken ct)
    {
        var ids = (roleIds ?? []).Concat(before?.RoleIds ?? []).Distinct().ToList();
        var names = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : (await session.Query<Role>().Where(r => ids.Contains(r.Id)).ToListAsync(ct)).ToDictionary(r => r.Id, r => r.Name);

        var metadata = new Dictionary<string, object>();
        if (roleIds is not null)
        {
            metadata["roleIds"] = roleIds.Select(id => id.ToString()).ToList();
            metadata["roleNames"] = roleIds.Select(id => names.GetValueOrDefault(id, string.Empty)).ToList();
        }

        if (before is not null)
        {
            metadata["previousStatus"] = before.Status.ToString();
            metadata["previousRoleIds"] = before.RoleIds.Select(id => id.ToString()).ToList();
            metadata["previousRoleNames"] = before.RoleIds.Select(id => names.GetValueOrDefault(id, string.Empty)).ToList();
        }

        return metadata;
    }

    /// <summary>A copy with the default comparer, so names stay case sensitive whatever the binder built.</summary>
    public static Dictionary<string, string> CopyOf(Dictionary<string, string>? profile) =>
        profile is null ? new() : new Dictionary<string, string>(profile, StringComparer.Ordinal);

    public static DateTimeOffset Instant(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static MemberResponse ToResponse(Membership membership, User? user) => new(
        membership.UserId,
        user?.Username ?? string.Empty,
        user?.Email ?? string.Empty,
        membership.RoleIds,
        membership.Status,
        Instant(membership.JoinedAt),
        membership.Profile ?? new());

    /// <summary>
    /// Queues a write of an existing membership that touches only the fields named, and no profile
    /// when <paramref name="profile"/> is null.
    /// </summary>
    /// <remarks>
    /// Storing the document that was read would write back every field it held at the read, and
    /// Membership has no version check. A profile is the one field here a request may leave alone,
    /// so a roles edit that read the row before another administrator cleared its profile would
    /// put the cleared attribute back, and the grant with it. A patch cannot: what the request
    /// does not carry is not in the statement. A version check was the other way to close it, and
    /// it would have made every other writer of Membership answer for a conflict too.
    ///
    /// The document passed in is brought up to date for the response only. The session is a
    /// lightweight one and does not track it.
    /// </remarks>
    private static void QueueWrite(
        IDocumentSession session,
        Membership membership,
        List<Guid> roleIds,
        MembershipStatus status,
        Dictionary<string, string>? profile)
    {
        session.Patch<Membership>(membership.Id).Set(x => x.RoleIds, roleIds);
        session.Patch<Membership>(membership.Id).Set(x => x.Status, status);

        membership.RoleIds = roleIds;
        membership.Status = status;

        if (profile is null)
            return;

        session.Patch<Membership>(membership.Id).Set(x => x.Profile, profile);
        membership.Profile = profile;
    }

    /// <summary>
    /// Adds what a write did to a profile to an audit entry: the names added, removed and given a
    /// new value. Nothing is added when the profile did not change.
    /// </summary>
    /// <remarks>
    /// Names and never values. A value can be a person's record id or ward, and the audit log is
    /// read more widely than the roster.
    /// </remarks>
    public static void RecordProfileChange(
        Dictionary<string, object> metadata,
        Dictionary<string, string>? before,
        Dictionary<string, string>? after)
    {
        var was = before ?? new Dictionary<string, string>();
        var now = after ?? new Dictionary<string, string>();

        var added = now.Keys.Where(name => !was.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        var removed = was.Keys.Where(name => !now.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        var changed = now
            .Where(pair => was.TryGetValue(pair.Key, out var old) && !string.Equals(old, pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();

        if (added.Count + removed.Count + changed.Count == 0)
            return;

        metadata["profileAdded"] = added;
        metadata["profileRemoved"] = removed;
        metadata["profileChanged"] = changed;
    }

    /// <summary>
    /// Stores a membership that did not exist and stages its audit entry on the same session.
    /// </summary>
    /// <remarks>
    /// <see cref="AddAsync"/>, <see cref="ChangeAsync"/> and <see cref="RemoveAsync"/> are the only
    /// writers of a membership, and each stages the entry beside the write, for the caller's one
    /// save. An endpoint that wrote a membership itself is how a grant came to have no entry
    /// (creating a tenant did), so <c>MembershipWriterTests</c> fails on a write anywhere else.
    /// </remarks>
    public static async Task<Membership> AddAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, Guid userId, string slug,
        List<Guid> roleIds, Dictionary<string, string>? profile, Dictionary<string, object> details,
        CancellationToken ct)
    {
        var membership = new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = slug,
            RoleIds = roleIds,
            Status = MembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            Profile = CopyOf(profile),
        };

        session.Store(membership);
        await RecordAsync(session, actor, "tenant.member.added", membership, roleIds, null, null, details, ct);
        return membership;
    }

    /// <summary>
    /// Queues a change to an existing membership through <see cref="QueueWrite"/> and stages its
    /// audit entry, which says what the member held before. See <see cref="AddAsync"/>.
    /// </summary>
    public static async Task ChangeAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string action,
        Membership membership, List<Guid> roleIds, MembershipStatus status,
        Dictionary<string, string>? profile, Dictionary<string, object> details, CancellationToken ct)
    {
        var before = Held.By(membership);
        var profileBefore = membership.Profile;

        QueueWrite(session, membership, roleIds, status, profile);
        await RecordAsync(session, actor, action, membership, roleIds, before, profileBefore, details, ct);
    }

    /// <summary>
    /// Marks a membership Removed, touching no other field, and stages its audit entry. See
    /// <see cref="AddAsync"/>.
    /// </summary>
    public static async Task RemoveAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, Membership membership,
        CancellationToken ct)
    {
        var before = Held.By(membership);

        session.Patch<Membership>(membership.Id).Set(x => x.Status, MembershipStatus.Removed);
        await RecordAsync(
            session, actor, "tenant.member.removed", membership, null, before, membership.Profile,
            new Dictionary<string, object>(), ct);
    }

    private static async Task RecordAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string action,
        Membership membership, IReadOnlyCollection<Guid>? roleIds, Held? before,
        Dictionary<string, string>? profileBefore, Dictionary<string, object> details, CancellationToken ct)
    {
        var metadata = await AuditMetadataAsync(session, roleIds, before, ct);
        foreach (var (key, value) in details)
            metadata[key] = value;
        RecordProfileChange(metadata, profileBefore, membership.Profile);

        Guid.TryParse(actor.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, membership.TenantSlug, action, actorId,
            actor.FindFirst("Username")?.Value,
            targetType: "User", targetId: membership.UserId.ToString(), metadata: metadata, ct: ct);
    }
}

/// <summary>
/// A profile is checked before anything is stored, on both member writes.
/// </summary>
/// <remarks>
/// A permission condition trusts these values (<c>$CURRENT_USER.&lt;name&gt;</c>), which is why they
/// are written only here, behind <c>manage_tenant_members</c>. A member holding that capability can
/// set their own profile, which is within what they can already do by assigning themselves a role.
/// A member without it cannot.
/// </remarks>
internal sealed class AddMemberValidator : Validator<AddMemberRequest>
{
    public AddMemberValidator()
    {
        RuleFor(x => x.Profile).Custom((profile, context) =>
        {
            if (CallerAttributes.ProfileError(profile) is { } error)
                context.AddFailure(error);
        });
    }
}

/// <inheritdoc cref="AddMemberValidator"/>
internal sealed class UpdateMemberValidator : Validator<UpdateMemberRequest>
{
    public UpdateMemberValidator()
    {
        RuleFor(x => x.Profile).Custom((profile, context) =>
        {
            if (CallerAttributes.ProfileError(profile) is { } error)
                context.AddFailure(error);
        });
    }
}

/// <summary>GET /api/tenants/members: the roster for the caller's tenant, newest first.</summary>
internal sealed class ListMembersEndpoint(
    IQuerySession session,
    TenantContext tenant) : Endpoint<ListRequest, PaginatedResponse<MemberResponse>>
{
    public override void Configure()
    {
        Get("/api/tenants/members");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;

        // Membership is SingleTenanted (it maps global users to tenants, so it cannot live inside a
        // tenant's partition). The slug filter is therefore the whole isolation guarantee on this
        // query, not a convenience on top of one Marten applies.
        var memberships = await session.Query<Membership>()
            .Where(m => m.TenantSlug == slug && m.Status != MembershipStatus.Removed)
            .ToListAsync(ct);

        var userIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = (await session.Query<User>()
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

    /// <summary>
    /// The member's whole profile. Left out, a new member or one who had been removed starts with
    /// none, and a member who is already here keeps the one they have.
    /// </summary>
    public Dictionary<string, string>? Profile { get; set; }
}

/// <summary>
/// POST /api/tenants/members: add a person to the caller's tenant by email.
/// </summary>
internal sealed class AddMemberEndpoint(
    IDocumentSession session,
    TenantContext tenant,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<AddMemberRequest, MemberResponse>
{
    public override void Configure()
    {
        Post("/api/tenants/members");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(AddMemberRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            AddError(r => r.Email, "A valid email address is required.");

        var roleIds = (req.RoleIds ?? new List<Guid>()).Distinct().ToList();
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            AddError(r => r.RoleIds, "SuperAdmin is a platform role and cannot be granted inside a tenant.");

        ThrowIfAnyErrors();

        if (roleIds.Count > 0)
        {
            var known = await session.Query<Role>().Where(r => roleIds.Contains(r.Id)).CountAsync(ct);
            if (known != roleIds.Count)
            {
                AddError(r => r.RoleIds, "One or more roles do not exist.");
                ThrowIfAnyErrors();
            }
        }

        var user = await session.Query<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == email, ct);
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
            session.Store(user);
        }

        var membership = await session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantSlug == slug, ct);

        if (await Members.RefusesPlatformRolesAsync(session, User, roleIds, membership, ct))
            ThrowError(barakoCMS.Features.Users.PlatformRoles.PlatformRoleRefusedMessage, 403);

        var details = new Dictionary<string, object> { ["invited"] = invited };

        if (membership is null)
        {
            membership = await Members.AddAsync(session, User, user.Id, slug, roleIds, req.Profile, details, ct);
        }
        else
        {
            // Re-adding somebody who was removed reactivates the row they already have. A second
            // row for the same pair would make EffectiveRoleIdsAsync depend on which one it read
            // first, and would lose the date they originally joined.
            //
            // Somebody coming back starts with the profile this request carries, or none: what
            // they held before they were removed is not what they are being given now. Somebody
            // who never left keeps theirs unless the request sends one, the same as an update.
            var returning = membership.Status == MembershipStatus.Removed;

            await Members.ChangeAsync(session, User, "tenant.member.added", membership, roleIds,
                MembershipStatus.Active,
                req.Profile is not null || returning ? Members.CopyOf(req.Profile) : null, details, ct);
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Two requests adding the same person at once both read no membership, and the unique
            // index on user and tenant refused the second. The same goes for two invites of one new
            // address and the unique email. Nothing from this request was stored.
            ThrowError(ConcurrentAdd, 409);
        }

        permissions.InvalidateUserPermissions(user.Id);

        await Send.OkAsync(Members.ToResponse(membership, user), ct);
    }

    internal const string ConcurrentAdd =
        "Another request added this person to the tenant at the same time. Read the member list and try again if needed.";

    /// <summary>A Postgres unique violation (SQLSTATE 23505), at whatever depth Marten wrapped it.</summary>
    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
                return true;
        }

        return false;
    }

    /// <summary>
    /// NormalizedUsername carries a unique index, so an invited address that happens to match an existing
    /// username would fail the insert with a 500 instead of adding the member.
    /// </summary>
    private async Task<string> AvailableUsernameAsync(string email, CancellationToken ct)
    {
        if (!await session.Query<User>().AnyAsync(u => u.NormalizedUsername == email, ct))
            return email;

        return $"{email}+{Guid.NewGuid():N}"[..(email.Length + 9)];
    }
}

internal sealed class UpdateMemberRequest
{
    public Guid UserId { get; set; }
    public List<Guid> RoleIds { get; set; } = new();
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;

    /// <summary>
    /// The member's whole profile. Left out, the stored one is kept, so a client that does not
    /// know about profiles cannot erase one by editing roles.
    /// </summary>
    public Dictionary<string, string>? Profile { get; set; }
}

/// <summary>
/// PUT /api/tenants/members/{userId}: change a member's roles, status or profile within the caller's tenant.
/// </summary>
internal sealed class UpdateMemberEndpoint(
    IDocumentSession session,
    TenantContext tenant,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<UpdateMemberRequest, MemberResponse>
{
    public override void Configure()
    {
        Put("/api/tenants/members/{userId}");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateMemberRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;

        var roleIds = (req.RoleIds ?? new List<Guid>()).Distinct().ToList();
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            AddError(r => r.RoleIds, "SuperAdmin is a platform role and cannot be granted inside a tenant.");

        if (req.Status == MembershipStatus.Removed)
            AddError(r => r.Status, "Use DELETE /api/tenants/members/{userId} to remove a member.");

        ThrowIfAnyErrors();

        if (roleIds.Count > 0)
        {
            var known = await session.Query<Role>().Where(r => roleIds.Contains(r.Id)).CountAsync(ct);
            if (known != roleIds.Count)
            {
                AddError(r => r.RoleIds, "One or more roles do not exist.");
                ThrowIfAnyErrors();
            }
        }

        var membership = await session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == req.UserId
                                      && m.TenantSlug == slug
                                      && m.Status != MembershipStatus.Removed, ct);
        if (membership is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await Members.RefusesPlatformRolesAsync(session, User, roleIds, membership, ct))
            ThrowError(barakoCMS.Features.Users.PlatformRoles.PlatformRoleRefusedMessage, 403);

        await Members.ChangeAsync(session, User, "tenant.member.updated", membership, roleIds, req.Status,
            req.Profile is null ? null : Members.CopyOf(req.Profile),
            new Dictionary<string, object> { ["status"] = req.Status.ToString() }, ct);

        await session.SaveChangesAsync(ct);
        permissions.InvalidateUserPermissions(req.UserId);

        var user = await session.LoadAsync<User>(req.UserId, ct);
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
internal sealed class RemoveMemberEndpoint(
    IDocumentSession session,
    TenantContext tenant,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<RemoveMemberRequest, RemoveMemberResponse>
{
    public override void Configure()
    {
        Delete("/api/tenants/members/{userId}");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RemoveMemberRequest req, CancellationToken ct)
    {
        var slug = tenant.Slug;

        var membership = await session.Query<Membership>()
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
        await Members.RemoveAsync(session, User, membership, ct);

        await session.SaveChangesAsync(ct);
        permissions.InvalidateUserPermissions(req.UserId);

        await Send.OkAsync(new RemoveMemberResponse { Message = "Member removed from this tenant." }, ct);
    }
}

/// <summary>
/// GET /api/tenants/members/roles: the roles an administrator may assign inside a tenant.
/// </summary>
internal sealed class AssignableRolesEndpoint(
    IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<AssignableRoleResponse>>
{
    public override void Configure()
    {
        Get("/api/tenants/members/roles");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var roles = await session.Query<Role>().OrderBy(r => r.Name).ToListAsync(ct);
        var superAdmin = await barakoCMS.Features.Users.PlatformRoles.IsSuperAdminAsync(session, User, ct);

        // Filtered by the same predicate the write paths refuse on, so the list a client is offered
        // and the list the server accepts cannot drift apart.
        var assignable = roles
            .Where(r => Members.IsAssignable(r.Id))
            .Where(r => superAdmin || !barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability(r))
            .Select(r => new AssignableRoleResponse(r.Id, r.Name, r.Description))
            .ToList();

        await Send.OkAsync(assignable.ToPagedResponse(req), ct);
    }
}
