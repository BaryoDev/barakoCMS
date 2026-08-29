namespace barakoCMS.Features.Roles.Delete;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
