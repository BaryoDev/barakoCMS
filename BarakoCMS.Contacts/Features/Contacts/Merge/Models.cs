namespace BarakoCMS.Contacts.Features.Contacts.Merge;

internal sealed class MergeContactRequest
{
    /// <summary>The duplicate, which stops being a contact.</summary>
    public Guid Id { get; set; }

    /// <summary>The contact that stays and takes what the duplicate had that it lacked.</summary>
    public Guid IntoId { get; set; }
}
