namespace barakoCMS.Features.Roles.Restore;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
