namespace barakoCMS.Features.Content.Get;

public class Request
{
    public Guid Id { get; set; }
}

public class Response
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public barakoCMS.Models.ContentStatus Status { get; set; }
    public Guid LastModifiedBy { get; set; }
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; }
}
