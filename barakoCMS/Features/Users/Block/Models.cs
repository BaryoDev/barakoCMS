namespace barakoCMS.Features.Users.Block;

internal class Request
{
    /// <summary>Bound from the {userId} route segment.</summary>
    public Guid UserId { get; set; }
}
