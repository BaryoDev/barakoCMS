namespace barakoCMS.Features.Roles.Delete;

public class Request
{
    public Guid Id { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
