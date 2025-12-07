using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Get;

public class Request
{
    public Guid Id { get; set; }
}

public class Response
{
    public Role? Role { get; set; }
}
