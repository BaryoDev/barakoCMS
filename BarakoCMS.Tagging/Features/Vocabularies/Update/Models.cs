namespace BarakoCMS.Tagging.Features.Vocabularies.Update;

internal sealed class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
