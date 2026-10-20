namespace barakoCMS.Features.Workflows;

/// <summary>What an action is handed on a run a schedule started: no entry, and the due time.</summary>
/// <remarks>
/// Its own type, like <see cref="ErasedContent"/>, so a template does not read the status and
/// timestamps of a new <see cref="Models.Content"/> as if an entry had them. It carries the due times
/// that <c>{{schedule.at}}</c> and <c>{{schedule.previousAt}}</c> read.
/// </remarks>
internal sealed class ScheduledContent : Models.Content
{
    public ScheduledContent(string contentType, DateTime at, DateTime? previousAt)
    {
        Id = Guid.Empty;
        ContentType = contentType;
        CreatedAt = default;
        UpdatedAt = default;
        At = at;
        PreviousAt = previousAt;
    }

    public DateTime At { get; }
    public DateTime? PreviousAt { get; }
}
