namespace barakoCMS.Features.UserGroups.Create;

public class Request
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Guid> UserIds { get; set; } = new();
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
