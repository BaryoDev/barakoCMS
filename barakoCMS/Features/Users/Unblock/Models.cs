namespace barakoCMS.Features.Users.Unblock;

internal class Request
{
    /// <summary>Bound from the {userId} route segment.</summary>
    public Guid UserId { get; set; }
}
