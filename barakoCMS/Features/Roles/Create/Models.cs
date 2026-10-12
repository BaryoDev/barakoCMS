namespace barakoCMS.Features.Roles.Create;

internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<barakoCMS.Models.ContentTypePermission> Permissions { get; set; } = new();
    public List<string> SystemCapabilities { get; set; } = new();
    public bool RequireMfa { get; set; }

    /// <summary>
    /// Make a platform role every tenant shares, rather than a role of the caller's tenant. Needs the
    /// <c>manage_platform_roles</c> capability.
    /// </summary>
    public bool IsPlatform { get; set; }
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
