using barakoCMS.Models;

namespace barakoCMS.Features.Roles;

/// <summary>
/// A role as the API describes it, rather than as it is stored.
/// </summary>
/// <remarks>
/// Roles used to go out as the Marten document. Two problems with that, and they pull in opposite
/// directions. Renaming a stored property becomes a silent wire break once 4.0 freezes the contract,
/// and adding a stored-only property leaks it to every client. Both are solved by the endpoint owning
/// its own shape.
///
/// It also gives <see cref="IsSystem"/> somewhere to live. Whether a role can be deleted is decided
/// by the server, keyed on the ids the seeder used, and the admin was re-deriving it from a
/// hardcoded list of names. That is wrong in both directions: rename a system role and the admin
/// offers a delete the server refuses, create a custom role called "HR" and the admin locks a role
/// the server would happily remove. The server knows; it should say.
/// </remarks>
internal sealed class RoleResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<ContentTypePermission> Permissions { get; init; } = new();
    public List<string> SystemCapabilities { get; init; } = new();
    public bool RequireMfa { get; init; }
    public bool Invitable { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The tenant that owns the role, or null for a platform role.</summary>
    public string? TenantSlug { get; init; }

    /// <summary>A role every tenant shares. Changing it needs <c>manage_platform_roles</c>.</summary>
    public bool IsPlatform { get; init; }

    /// <summary>Whether the server refuses to delete this role. Not stored, derived per request.</summary>
    public bool IsSystem { get; init; }

    /// <summary>When the role was deleted. Null for a live role, which is every role outside the deleted list.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    public static RoleResponse From(Role role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        Permissions = role.Permissions,
        SystemCapabilities = role.SystemCapabilities,
        RequireMfa = role.RequireMfa,
        Invitable = role.Invitable,
        // Stored as DateTime, emitted with a zone. Every timestamp this API returns names an
        // unambiguous instant, which is the rule DateWireFormatTests holds.
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(role.CreatedAt, DateTimeKind.Utc)),
        IsSystem = SystemRoles.Contains(role.Id),
        TenantSlug = RoleScope.OwnerOf(role),
        IsPlatform = role.IsPlatform,
        DeletedAt = role.DeletedAt is { } deleted ? new DateTimeOffset(DateTime.SpecifyKind(deleted, DateTimeKind.Utc)) : null,
    };
}
