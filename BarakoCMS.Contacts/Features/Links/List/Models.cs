using barakoCMS.Models;

namespace BarakoCMS.Contacts.Features.Links.List;

internal sealed class ListLinksRequest : PaginatedRequest
{
    /// <summary>The contact, as the person or as the organization.</summary>
    public Guid Id { get; set; }
}
