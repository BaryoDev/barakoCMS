namespace barakoCMS.Features.UserGroups.Create;

internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Guid> UserIds { get; set; } = new();
    public List<Guid> RoleIds { get; set; } = new();
}

internal class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
