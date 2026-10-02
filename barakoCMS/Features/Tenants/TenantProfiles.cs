using System.Text.Json;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using Marten;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Tenants;

/// <summary>A tenant's public profile, as the routes that still serve it answer.</summary>
internal sealed record TenantProfile(
    string? LogoUrl,
    string? About,
    string? Location,
    string? LocationUrl,
    string? SocialHandle,
    string? Email,
    string? ContactUrl);

/// <summary>Reads a tenant's public profile from its <c>site</c> entry.</summary>
/// <remarks>
/// The entry is read the way <c>GET /api/public/site</c> delivers it: the published entry of a
/// publicly deliverable type, and only the fields that type marks Public. So a route answering from
/// here shows nothing anonymous delivery does not already show.
///
/// Each field falls back to the <see cref="Tenant"/> document on its own. Until
/// <c>migrations/4.6.0/tenant-profile-to-site.sql</c> has run, and for a tenant it left alone (no
/// published site entry, say), the document still holds the values. The file blanks each value it
/// moves, which is what makes a per-field fallback safe: once a value has moved, clearing it in the
/// site entry leaves nothing behind to come back. The fallback goes when the obsolete members do.
/// </remarks>
internal static class TenantProfiles
{
    public const string SiteType = "site";

    public static async Task<TenantProfile> ReadAsync(IDocumentStore store, Tenant tenant, CancellationToken ct)
    {
        var site = await PublishedSiteAsync(store, tenant.Slug, ct);

#pragma warning disable CS0618 // the fallback for a document whose values have not been moved yet
        return new TenantProfile(
            LogoUrl: Text(site, "Logo") ?? tenant.LogoUrl,
            About: Text(site, "About") ?? tenant.About,
            Location: Text(site, "Location") ?? tenant.Location,
            LocationUrl: Text(site, "LocationUrl") ?? tenant.LocationUrl,
            SocialHandle: Text(site, "SocialHandle") ?? tenant.SocialHandle,
            Email: Text(site, "Email") ?? tenant.Email,
            ContactUrl: Text(site, "ContactUrl") ?? tenant.ContactUrl);
#pragma warning restore CS0618
    }

    /// <summary>The public fields of the tenant's published site entry, or null when it has none.</summary>
    private static async Task<Dictionary<string, object>?> PublishedSiteAsync(
        IDocumentStore store, string slug, CancellationToken ct)
    {
        // The tenant is named, never taken from the request: two of the callers run on a route that
        // may resolve no tenant, and each reads tenants other than the one the caller is in.
        await using var session = slug == Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(slug);

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == SiteType, ct);
        if (!PublicDelivery.IsDeliverable(definition))
            return null;

        var entry = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == SiteType
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entry is null ? null : PublicDelivery.ToPublic(entry, definition, slugField: null)?.Data;
    }

    /// <summary>The field's value when it is text and not blank.</summary>
    /// <remarks>
    /// Matched without regard to case, as validation and delivery match a key to its field, so a
    /// value stored as <c>about</c> is found. A blank falls through to the next key and then to the
    /// tenant document.
    /// </remarks>
    private static string? Text(Dictionary<string, object>? site, string field)
    {
        if (site is null)
            return null;

        foreach (var (key, value) in site)
        {
            if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                continue;

            var text = value switch
            {
                string plain => plain,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }
}
