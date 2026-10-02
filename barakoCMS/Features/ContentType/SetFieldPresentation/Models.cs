namespace barakoCMS.Features.ContentType.SetFieldPresentation;

/// <remarks>
/// The three are set together. A member left out is null, and null clears it, so a caller changing
/// one sends the other two as they stand.
/// </remarks>
internal class Request
{
    /// <summary>One of <c>fieldEditors</c> in <c>GET /api/meta/describe</c>, or null for none.</summary>
    public string? Editor { get; set; }

    /// <summary>The group the field sits in on a generated edit screen, or null for none.</summary>
    public string? Section { get; set; }

    /// <summary>One of <c>fieldRoles</c> in <c>GET /api/meta/describe</c>, or null for none.</summary>
    public string? Role { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string? Editor { get; set; }
    public string? Section { get; set; }
    public string? Role { get; set; }
}
