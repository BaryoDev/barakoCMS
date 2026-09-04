namespace barakoCMS.Features.Roles.Create;

internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();
    public List<string> SystemCapabilities { get; set; } = new();
}

internal class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The capability names in the request that no endpoint on this instance asks for. Saved anyway
    /// unless <c>Roles:RefuseUnknownCapabilities</c> is on, so a console can show them.
    /// </summary>
    public List<string> UnknownCapabilities { get; set; } = new();
}
