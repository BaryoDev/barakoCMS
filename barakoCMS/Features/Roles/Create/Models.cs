namespace barakoCMS.Features.Roles.Create;

public class Request
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();
    public List<string> SystemCapabilities { get; set; } = new();
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
