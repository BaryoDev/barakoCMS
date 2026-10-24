using barakoCMS.Models;

namespace BarakoCMS.Tagging.Features.Assignments.ForRecord;

internal sealed class Request : PaginatedRequest
{
    public string ContentType { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
}
