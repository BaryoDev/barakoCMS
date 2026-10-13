using System.Security.Claims;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Data;

/// <summary>
/// The memberships a seed file declares, written through <see cref="Members"/>, which stages the
/// audit entry beside each write. Apart from <see cref="SeedFile"/> because a file that reads
/// memberships stores nothing itself (<c>MembershipWriterTests</c>).
/// </summary>
internal static class SeedMemberships
{
    private static readonly ClaimsPrincipal Actor = new(new ClaimsIdentity([new Claim("Username", "seed-file")]));

    /// <summary>Gives the user exactly <paramref name="roleIds"/> in the tenant, active, and leaves a membership that already has them alone.</summary>
    public static async Task GrantAsync(IDocumentSession session, string slug, Guid userId, List<Guid> roleIds, CancellationToken ct)
    {
        var details = new Dictionary<string, object> { ["seedFile"] = true };
        var membership = await session.Query<Membership>().FirstOrDefaultAsync(m => m.UserId == userId && m.TenantSlug == slug, ct);

        if (membership is null)
        {
            await Members.AddAsync(session, Actor, userId, slug, roleIds, profile: null, details, ct);
        }
        else if (membership.Status != MembershipStatus.Active || !membership.RoleIds.Order().SequenceEqual(roleIds.Order()))
        {
            await Members.ChangeAsync(session, Actor, "tenant.member.updated", membership, roleIds, MembershipStatus.Active, profile: null, details, ct);
        }
    }
}
