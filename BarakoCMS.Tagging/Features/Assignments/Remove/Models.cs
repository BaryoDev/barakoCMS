namespace BarakoCMS.Tagging.Features.Assignments.Remove;

internal sealed class Request
{
    public string ContentType { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public Guid TermId { get; set; }
}
