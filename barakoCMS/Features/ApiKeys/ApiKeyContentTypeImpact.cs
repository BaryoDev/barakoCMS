using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.ApiKeys;

internal sealed record ApiKeyWarning(Guid KeyId, string Name, List<string> ContentTypes);

/// <summary>
/// Which live keys of a tenant name a content type. A change that renames or removes the type
/// leaves those keys unable to push, so the response to that change lists them.
/// </summary>
internal static class ApiKeyContentTypeImpact
{
    public static async Task<List<ApiKeyWarning>> KeysNamingAsync(
        IQuerySession session, string tenant, IReadOnlyCollection<string> contentTypes, CancellationToken ct)
    {
        var keys = await session.Query<ApiKey>()
            .Where(k => k.TenantSlug == tenant && !k.Revoked)
            .ToListAsync(ct);

        return keys
            .Select(k => new ApiKeyWarning(k.Id, k.Name, (k.ContentTypes ?? [])
                .Where(t => contentTypes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()))
            .Where(w => w.ContentTypes.Count > 0)
            .ToList();
    }
}
