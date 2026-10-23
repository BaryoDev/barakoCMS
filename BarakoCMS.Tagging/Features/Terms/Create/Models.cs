namespace BarakoCMS.Tagging.Features.Terms.Create;

internal sealed class Request
{
    public Guid VocabularyId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>A term of the same vocabulary to nest under. Left out, the term is at the top.</summary>
    public Guid? ParentId { get; set; }
}
