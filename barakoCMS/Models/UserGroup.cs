namespace barakoCMS.Models;

public class UserGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Guid> RoleIds { get; set; } = new();
    public List<Guid> MemberIds { get; set; } = new();
}
