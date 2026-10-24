using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>Reads a tenant's allowed sign-in methods, for every path that starts a sign-in.</summary>
/// <remarks>
/// Public because the ExternalAuth module asks it, the same way it asks <see cref="SignUpPolicy"/>.
/// The default tenant and a slug nobody registered allow every method.
/// </remarks>
public static class TenantSignIn
{
    public static async Task<bool> AllowsAsync(IQuerySession session, string? tenantSlug, string method, CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(tenantSlug) ? Tenant.DefaultSlug : tenantSlug.Trim().ToLowerInvariant();
        if (slug == Tenant.DefaultSlug)
        {
            return true;
        }

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        return tenant is null || tenant.SignIn.Allows(method);
    }

    /// <summary>
    /// Whether the tenant names <paramref name="method"/>. For a method a module adds, which is off
    /// until named. The default tenant and a slug nobody registered name nothing, so this is false. So is a tenant that is not active.
    /// </summary>
    public static async Task<bool> AllowsExplicitlyAsync(IQuerySession session, string? tenantSlug, string method, CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(tenantSlug) ? Tenant.DefaultSlug : tenantSlug.Trim().ToLowerInvariant();
        if (slug == Tenant.DefaultSlug)
        {
            return false;
        }

        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        return tenant is not null && tenant.Status == TenantStatus.Active && tenant.SignIn.AllowsExplicitly(method);
    }

    public static string RefusedMessage(string method) => $"This tenant does not allow {method} sign-in.";
}
