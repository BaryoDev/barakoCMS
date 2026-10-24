namespace BarakoCMS.Tagging.Features.Vocabularies.Create;

internal sealed class Request
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
