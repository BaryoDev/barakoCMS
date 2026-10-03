using barakoCMS.Models;

namespace barakoCMS.Features.Tenants;

/// <summary>A tenant as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning. This one has the sharpest version of
/// the leak argument: <see cref="Tenant"/> is a settings document that will keep growing, and every
/// property added to it would otherwise appear on the wire the moment it is stored, whether or not
/// anybody decided it should be public.
///
/// It carries what routing and administration need. The public profile is not here: it lives in the
/// tenant's <c>site</c> entry and is read from there.
/// </remarks>
internal sealed class TenantResponse
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Branding { get; init; } = new();
    public List<string> Domains { get; init; } = new();
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static TenantResponse From(Tenant t) => new()
    {
        Id = t.Id,
        Slug = t.Slug,
        Name = t.Name,
#pragma warning disable CS0618 // still returned until the member goes, since nothing replaced it
        Branding = t.Branding,
#pragma warning restore CS0618
        Domains = t.Domains,
        IsActive = t.IsActive,
        // Stored as DateTime, emitted with a zone, like every other timestamp this API returns.
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
    };
}
