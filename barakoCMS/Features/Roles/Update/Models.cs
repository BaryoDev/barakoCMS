namespace barakoCMS.Features.Roles.Update;

public class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();
    public List<string> SystemCapabilities { get; set; } = new();
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
