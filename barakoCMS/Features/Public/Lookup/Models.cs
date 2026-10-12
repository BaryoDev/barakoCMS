namespace barakoCMS.Features.Public.Lookup;

internal sealed class Request
{
    /// <summary>The value of the type's match field, for example a registration number.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The value of any one of the type's verify fields, for example a last name or an email.</summary>
    public string Verify { get; set; } = string.Empty;
}

internal sealed class Response
{
    /// <summary>The fields the lookup returns, plus the reveal fields while the reveal is on.</summary>
    public Dictionary<string, object> Data { get; set; } = new();
}
