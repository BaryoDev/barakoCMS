namespace barakoCMS.Features.Workflows;

/// <summary>What an action is handed on a Deleted run: the erased entry's id and content type.</summary>
/// <remarks>
/// Its own type so an action can tell it from a loaded entry. A plain <see cref="Models.Content"/>
/// defaults to a status of Draft and timestamps of now, and an action that sent those would report
/// an erasure as a move to Draft at the moment the action ran. The timestamps are cleared here, and
/// the webhook, the template variables and the conditional action each check for this type before
/// they read the status, the timestamps or the data.
/// </remarks>
internal sealed class ErasedContent : Models.Content
{
    public ErasedContent(Guid id, string contentType)
    {
        Id = id;
        ContentType = contentType;
        CreatedAt = default;
        UpdatedAt = default;
    }
}
