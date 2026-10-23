using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Notifications;

internal sealed class RecipientResolver(IQuerySession session, TenantContext tenant)
{
    public async Task<bool> IsInTenantAsync(Guid userId, CancellationToken ct)
    {
        if (tenant.IsDefault)
            return await session.Query<User>().AnyAsync(u => u.Id == userId, ct);

        return await session.Query<Membership>().AnyAsync(
            m => m.UserId == userId && m.TenantSlug == tenant.Slug && m.Status == MembershipStatus.Active, ct);
    }
}
