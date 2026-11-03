using barakoCMS.Events;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.ChangeStatus;

internal static class ScheduleClearing
{
    /// <summary>
    /// The status event, followed by an event that drops the pending publish time when the entry is
    /// moving anywhere but Scheduled. The unpublish time is kept, as the scheduler keeps it.
    /// </summary>
    /// <remarks>
    /// A status set by hand leaves ScheduledPublishAt armed otherwise, and the scheduler then
    /// publishes the entry at that time whatever it was moved to.
    /// </remarks>
    public static List<object> With(Models.Content content, ContentStatusChanged statusChanged, ContentStatus newStatus, Guid userId)
    {
        var events = new List<object> { statusChanged };
        if (newStatus != ContentStatus.Scheduled && content.ScheduledPublishAt is not null)
        {
            events.Add(new ContentScheduled(content.Id, null, content.ScheduledUnpublishAt, userId, DateTime.UtcNow));
        }

        return events;
    }
}
