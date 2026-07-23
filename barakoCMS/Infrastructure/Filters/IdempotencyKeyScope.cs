using System.Security.Claims;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Builds the stored key for an idempotent request from the client's raw Idempotency-Key.
///
/// The raw key is namespaced by tenant and user so it is unique <em>to the caller</em>, not
/// globally. Without this, tenant B reusing a key tenant A already used gets a spurious 409, and one
/// user could probe another's key space.
/// </summary>
internal static class IdempotencyKeyScope
{
    // ASCII unit separator: delimits the parts so "a"+"bc" can't collide with "ab"+"c". It won't
    // occur in a slug, a GUID, or a sane client key.
    private const char Sep = '';

    public static string Build(HttpContext http, string rawKey)
    {
        var tenant = http.RequestServices.GetService<TenantContext>()?.Slug ?? Models.Tenant.DefaultSlug;

        // Prefer the stable user id; fall back to the username, then to "anon" for unauthenticated
        // POSTs (rare, but the header is still honoured).
        var user = http.User;
        var userId = user?.FindFirst("UserId")?.Value
                     ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? user?.FindFirst("Username")?.Value
                     ?? "anon";

        return string.Join(Sep, tenant, userId, rawKey);
    }
}
