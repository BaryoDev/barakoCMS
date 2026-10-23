namespace BarakoCMS.Tagging.Features.Terms.Update;

internal sealed class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The term to nest under. Left out, the term moves to the top.</summary>
    public Guid? ParentId { get; set; }
}
