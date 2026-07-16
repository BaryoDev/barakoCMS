using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Multitenancy;

namespace barakoCMS.Features.Club;

// Per-club membership management, run by a club's own officers (users holding "Admin" in the club).
// Every endpoint is scoped to the caller's current tenant (TenantContext), and TenantAccessMiddleware
// guarantees the caller's token was minted for that tenant — so a club officer can only ever manage
// their own club's roster. Assigning the platform "SuperAdmin" role is never allowed here.

public sealed record ClubMember(
    Guid UserId, string Email, string Username, List<string> Roles, string Status, DateTime JoinedAt);

public sealed record AssignableRole(Guid Id, string Name);

/// <summary>GET /api/club/roles — roles a club officer may assign (excludes platform SuperAdmin).</summary>
public class ListClubRolesEndpoint : EndpointWithoutRequest<List<AssignableRole>>
{
    private readonly IQuerySession _session;
    public ListClubRolesEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/club/roles");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var roles = await _session.Query<Role>().ToListAsync(ct);
        var assignable = roles
            .Where(r => r.Name != "SuperAdmin")
            .Select(r => new AssignableRole(r.Id, r.Name))
            .OrderBy(r => r.Name)
            .ToList();
        await SendOkAsync(assignable, ct);
    }
}

/// <summary>GET /api/club/members — the current club's roster (active + suspended).</summary>
public class ListClubMembersEndpoint : EndpointWithoutRequest<List<ClubMember>>
{
    private readonly IQuerySession _session;
    private readonly TenantContext _tenant;
    public ListClubMembersEndpoint(IQuerySession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Get("/api/club/members");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = _tenant.Slug;
        var memberships = (await _session.Query<Membership>()
                .Where(m => m.TenantSlug == slug && m.Status != MembershipStatus.Removed)
                .ToListAsync(ct))
            .OrderByDescending(m => m.JoinedAt)
            .ToList();

        var userIds = memberships.Select(m => m.UserId).ToList();
        var users = (await _session.Query<User>().Where(u => userIds.Contains(u.Id)).ToListAsync(ct))
            .ToDictionary(u => u.Id);
        var roleNames = (await _session.Query<Role>().ToListAsync(ct))
            .ToDictionary(r => r.Id, r => r.Name);

        var result = memberships.Select(m =>
        {
            users.TryGetValue(m.UserId, out var u);
            return new ClubMember(
                m.UserId,
                u?.Email ?? "",
                u?.Username ?? "",
                m.RoleIds.Where(roleNames.ContainsKey).Select(id => roleNames[id]).OrderBy(n => n).ToList(),
                m.Status.ToString(),
                m.JoinedAt);
        }).ToList();

        await SendOkAsync(result, ct);
    }
}

public sealed class AddClubMemberRequest
{
    public string Email { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

/// <summary>POST /api/club/members — add someone to the club by email, creating an OTP-only user if new.</summary>
public class AddClubMemberEndpoint : Endpoint<AddClubMemberRequest, ClubMember>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    public AddClubMemberEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/club/members");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(AddClubMemberRequest req, CancellationToken ct)
    {
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ThrowError(r => r.Email, "A valid email is required.");
            return;
        }

        var slug = _tenant.Slug;
        var roleIds = await ResolveRolesAsync(_session, req.Roles, ct);

        var user = await _session.Query<User>().FirstOrDefaultAsync(u => u.Email.ToLower() == email, ct);
        if (user is null)
        {
            // OTP-only account: no password, they sign in with an emailed code.
            user = new User { Id = Guid.NewGuid(), Username = email, Email = email, PasswordHash = "" };
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
            membership.RoleIds = roleIds;
            membership.Status = MembershipStatus.Active;
        }
        _session.Store(membership);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(await ToMemberAsync(_session, membership, user, ct), ct);
    }

    internal static async Task<List<Guid>> ResolveRolesAsync(IQuerySession session, List<string> names, CancellationToken ct)
    {
        var wanted = (names ?? new()).Select(n => n?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (wanted.Count == 0) return new();
        var roles = await session.Query<Role>().ToListAsync(ct);
        // Never assignable from a club officer screen.
        return roles.Where(r => r.Name != "SuperAdmin" && wanted.Contains(r.Name)).Select(r => r.Id).ToList();
    }

    internal static async Task<ClubMember> ToMemberAsync(IQuerySession session, Membership m, User u, CancellationToken ct)
    {
        var names = (await session.Query<Role>().Where(r => m.RoleIds.Contains(r.Id)).ToListAsync(ct))
            .Select(r => r.Name).OrderBy(n => n).ToList();
        return new ClubMember(m.UserId, u.Email, u.Username, names, m.Status.ToString(), m.JoinedAt);
    }
}

public sealed class UpdateClubMemberRequest
{
    public List<string> Roles { get; set; } = new();
    public string? Status { get; set; }
}

/// <summary>PUT /api/club/members/{userId} — change a member's roles or status within the club.</summary>
public class UpdateClubMemberEndpoint : Endpoint<UpdateClubMemberRequest, ClubMember>
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    public UpdateClubMemberEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Put("/api/club/members/{userId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateClubMemberRequest req, CancellationToken ct)
    {
        var slug = _tenant.Slug;
        Guid.TryParse(Route<string>("userId"), out var userId);

        var membership = await _session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantSlug == slug, ct);
        if (membership is null) { await SendNotFoundAsync(ct); return; }

        membership.RoleIds = await AddClubMemberEndpoint.ResolveRolesAsync(_session, req.Roles, ct);
        if (!string.IsNullOrWhiteSpace(req.Status) &&
            Enum.TryParse<MembershipStatus>(req.Status, ignoreCase: true, out var status))
        {
            membership.Status = status;
        }
        _session.Store(membership);
        await _session.SaveChangesAsync(ct);

        var user = await _session.Query<User>().FirstOrDefaultAsync(u => u.Id == userId, ct);
        await SendOkAsync(await AddClubMemberEndpoint.ToMemberAsync(_session, membership, user!, ct), ct);
    }
}

/// <summary>DELETE /api/club/members/{userId} — remove a member from the club (marks them Removed).</summary>
public class RemoveClubMemberEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly TenantContext _tenant;
    public RemoveClubMemberEndpoint(IDocumentSession session, TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/club/members/{userId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = _tenant.Slug;
        Guid.TryParse(Route<string>("userId"), out var userId);

        var membership = await _session.Query<Membership>()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantSlug == slug, ct);
        if (membership is null) { await SendNotFoundAsync(ct); return; }

        membership.Status = MembershipStatus.Removed;
        _session.Store(membership);
        await _session.SaveChangesAsync(ct);
        await SendNoContentAsync(ct);
    }
}
