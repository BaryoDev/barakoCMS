using barakoCMS.Models;

namespace barakoCMS.Features.Roles.List;

public class Request
{
    // Empty for now, can add pagination later
}

public class Response
{
    public List<Role> Roles { get; set; } = new();
}
