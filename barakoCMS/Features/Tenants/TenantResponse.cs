using barakoCMS.Models;

namespace barakoCMS.Features.Tenants;

/// <summary>A tenant as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning. This one has the sharpest version of
/// the leak argument: <see cref="Tenant"/> is a settings document that will keep growing, and every
/// property added to it would otherwise appear on the wire the moment it is stored, whether or not
/// anybody decided it should be public.
/// </remarks>
internal sealed class TenantResponse
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? LogoUrl { get; init; }
    public string? About { get; init; }
    public string? Location { get; init; }
    public string? LocationUrl { get; init; }
    public string? SocialHandle { get; init; }
    public string? Email { get; init; }
    public string? ContactUrl { get; init; }
    public Dictionary<string, string> Branding { get; init; } = new();
    public List<string> Domains { get; init; } = new();
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static TenantResponse From(Tenant t) => new()
    {
        Id = t.Id,
        Slug = t.Slug,
        Name = t.Name,
        LogoUrl = t.LogoUrl,
        About = t.About,
        Location = t.Location,
        LocationUrl = t.LocationUrl,
        SocialHandle = t.SocialHandle,
        Email = t.Email,
        ContactUrl = t.ContactUrl,
        Branding = t.Branding,
        Domains = t.Domains,
        IsActive = t.IsActive,
        // Stored as DateTime, emitted with a zone, like every other timestamp this API returns.
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
    };
}
