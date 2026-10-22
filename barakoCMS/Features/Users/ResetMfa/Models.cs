namespace barakoCMS.Features.Users.ResetMfa;

internal class Request
{
    /// <summary>Bound from the {userId} route segment.</summary>
    public Guid UserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
