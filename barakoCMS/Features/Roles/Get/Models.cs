using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Get;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    public Role? Role { get; set; }
}
