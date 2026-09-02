using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups;

/// <summary>A user group as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning. Briefly: once 4.0 freezes the contract,
/// renaming a stored property is a silent wire break and adding a stored-only property leaks it to
/// every client. An endpoint that owns its shape has neither problem.
/// </remarks>
internal sealed class UserGroupResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<Guid> UserIds { get; init; } = new();
    public Guid? ParentGroupId { get; init; }
    public List<Guid> ChildGroupIds { get; init; } = new();

    public static UserGroupResponse From(UserGroup g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Description = g.Description,
        UserIds = g.UserIds,
        ParentGroupId = g.ParentGroupId,
        ChildGroupIds = g.ChildGroupIds,
    };
}
