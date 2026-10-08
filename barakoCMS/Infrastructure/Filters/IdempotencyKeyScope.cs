using System.Security.Claims;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Builds the stored key for an idempotent request from the client's raw Idempotency-Key.
///
/// The raw key is namespaced by tenant and caller so it is unique <em>to the caller</em>, not
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
        return string.Join(Sep, tenant, Caller(http) ?? "anon", rawKey);
    }

    /// <summary>Who the key belongs to, or null for an unauthenticated caller.</summary>
    /// <remarks>
    /// An API key is its own caller, apart from the user it acts for, so a key and that user's own
    /// session never answer each other's replays. Otherwise the stable user id, then the username.
    /// Every unauthenticated caller shares one bucket, which is why <see cref="IdempotencyFilter"/>
    /// never replays a stored response into it.
    /// </remarks>
    public static string? Caller(HttpContext http)
    {
        var user = http.User;
        var apiKeyId = user?.FindFirst("apikey_id")?.Value;
        if (apiKeyId is not null)
        {
            return "apikey:" + apiKeyId;
        }

        return user?.FindFirst("UserId")?.Value
               ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user?.FindFirst("Username")?.Value;
    }
}
