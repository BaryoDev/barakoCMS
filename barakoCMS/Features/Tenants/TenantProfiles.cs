using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using System.Text.Json;
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
/// Field by field, the site decides wherever it has something to say, and the <see cref="Tenant"/>
/// document answers only where it has not:
/// <list type="bullet">
/// <item>No published site entry, or a type that does not declare the field: the tenant document.
/// That is every tenant before <c>migrations/4.6.0/tenant-profile-to-site.sql</c> has run.</item>
/// <item>A declared field that is not Public: empty. Marking a field Sensitive is how a tenant takes
/// it off this route, and a value left on the tenant document must not undo that.</item>
/// <item>A declared, Public field the entry holds, blank included: the entry's value. An editor who
/// blanks the field has removed it.</item>
/// <item>A declared, Public field the entry has no key for: the tenant document. That is a field
/// nobody has set, such as <c>Logo</c> on a site made before the move.</item>
/// </list>
/// The three link members answer a site value only when it is an absolute http or https address.
/// The tenant API used to refuse anything else on write, and a site field can be declared as plain
/// text by the tenant's own administrator, so the rule is applied here, where it cannot be skipped.
///
/// The fallback goes when the obsolete members do.
/// </remarks>
internal static class TenantProfiles
{
    public const string SiteType = "site";

    public static async Task<TenantProfile> ReadAsync(IDocumentStore store, Tenant tenant, CancellationToken ct)
    {
        var site = await PublishedSiteAsync(store, tenant.Slug, ct);

#pragma warning disable CS0618 // the fallback for a document whose values have not been moved yet
        return new TenantProfile(
            LogoUrl: Pick(site, "Logo", tenant.LogoUrl, link: true),
            About: Pick(site, "About", tenant.About),
            Location: Pick(site, "Location", tenant.Location),
            LocationUrl: Pick(site, "LocationUrl", tenant.LocationUrl, link: true),
            SocialHandle: Pick(site, "SocialHandle", tenant.SocialHandle),
            Email: Pick(site, "Email", tenant.Email),
            ContactUrl: Pick(site, "ContactUrl", tenant.ContactUrl, link: true));
#pragma warning restore CS0618
    }

    /// <summary>
    /// Blanks each profile member an update sent as an empty string, and leaves the rest alone.
    /// </summary>
    /// <remarks>
    /// The one way to remove a value still on the tenant document through the API. Absent and null
    /// are the same thing to the binder and mean "not sent"; a string, which the validator has
    /// already held to blank, means "clear it".
    /// </remarks>
    public static void ClearBlanked(Tenant tenant, TenantWriteRequest req)
    {
#pragma warning disable CS0618 // clearing is the only write the obsolete members still get
        if (req.LogoUrl is not null) tenant.LogoUrl = null;
        if (req.About is not null) tenant.About = null;
        if (req.Location is not null) tenant.Location = null;
        if (req.LocationUrl is not null) tenant.LocationUrl = null;
        if (req.SocialHandle is not null) tenant.SocialHandle = null;
        if (req.Email is not null) tenant.Email = null;
        if (req.ContactUrl is not null) tenant.ContactUrl = null;
#pragma warning restore CS0618
    }

    /// <summary>What the published site entry says about the profile: its public data and its type's fields.</summary>
    private sealed record PublishedSite(Dictionary<string, object> Data, List<FieldDefinition> Fields);

    private static async Task<PublishedSite?> PublishedSiteAsync(IDocumentStore store, string slug, CancellationToken ct)
    {
        // The tenant is named, never taken from the request: two of the callers run on a route that
        // may resolve no tenant, and each reads tenants other than the one the caller is in.
        await using var session = slug == Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(slug);

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == SiteType, ct);
        if (definition is null || !PublicDelivery.IsDeliverable(definition))
            return null;

        var entry = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == SiteType
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return null;

        var delivered = PublicDelivery.ToPublic(entry, definition, slugField: null);
        return delivered is null ? null : new PublishedSite(delivered.Data, definition.Fields);
    }

    private static string? Pick(PublishedSite? site, string field, string? onTheTenant, bool link = false)
    {
        if (site is null)
            return onTheTenant;

        // Names are matched without regard to case, as validation and delivery match a key to its
        // field, so a field declared as "about" or a value stored under it is found.
        var declared = site.Fields
            .Where(f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (declared.Count == 0)
            return onTheTenant;

        if (declared.Any(f => f.Sensitivity != SensitivityLevel.Public))
            return null;

        var held = site.Data
            .Where(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToList();
        if (held.Count == 0)
            return onTheTenant;

        var text = held.Select(AsText).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (text is null)
            return null;

        return link && !TenantHandles.IsValidAbsoluteUrl(text) ? null : text;
    }

    private static string? AsText(object? value) => value switch
    {
        string plain => plain,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };
}
