using barakoCMS.Models;

namespace BarakoCMS.Tagging.Features.Assignments.Records;

internal sealed class Request : PaginatedRequest
{
    public Guid TermId { get; set; }

    /// <summary>Only records of this content type. Left out, records of every type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Also count records carrying a term nested under this one.</summary>
    public bool IncludeDescendants { get; set; }
}

internal sealed class Response : PaginatedResponse<AssignmentResponse>
{
    /// <summary>True when more assignments exist than the 5000 considered, so the list and total are of the newest ones only.</summary>
    public bool Truncated { get; set; }
}
