using barakoCMS.Models;

namespace BarakoCMS.Tagging.Features.Terms.List;

internal sealed class Request : PaginatedRequest
{
    public Guid VocabularyId { get; set; }

    /// <summary>Only the direct children of this term. Left out, every term of the vocabulary.</summary>
    public Guid? ParentId { get; set; }
}
