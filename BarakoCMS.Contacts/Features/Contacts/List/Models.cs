using barakoCMS.Models;

namespace BarakoCMS.Contacts.Features.Contacts.List;

internal sealed class ListContactsRequest : PaginatedRequest
{
    /// <summary>Only Person or only Organization.</summary>
    public string? Kind { get; set; }

    /// <summary>Contacts whose display name contains this text.</summary>
    public string? Name { get; set; }

    /// <summary>The contact holding exactly this email, if any.</summary>
    public string? Email { get; set; }
}
