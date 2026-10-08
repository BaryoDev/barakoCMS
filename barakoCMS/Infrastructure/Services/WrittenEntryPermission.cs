using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The second half of a write's permission check: the rule that let the caller change the entry
/// must also hold for the entry the write leaves behind.
/// </summary>
/// <remarks>
/// A rule with conditions, a branch field or <c>$CURRENT_USER</c> say, grants a write on the entries
/// it matches. Checking only the stored entry let a caller change the very field the rule filters on
/// and move the entry outside the rule, where they could not have written it in the first place.
/// The same evaluator decides both halves, so a rule cannot mean one thing before the write and
/// another after it.
///
/// Only the data is replaced on the copy. A status change is judged against the stored entry, as the
/// status route judges it, so a rule on status gives the same answer whichever route moves it.
/// </remarks>
internal static class WrittenEntryPermission
{
    public static Task<bool> AllowsWrittenEntryAsync(
        this IPermissionResolver permissions,
        User user,
        Models.Content existing,
        string action,
        IDictionary<string, object> data,
        CancellationToken ct)
        => permissions.CanPerformActionAsync(user, existing.ContentType, action, AsWritten(existing, data), ct);

    /// <summary>A copy of the stored entry holding the data the write is about to store.</summary>
    private static Models.Content AsWritten(Models.Content existing, IDictionary<string, object> data) => new()
    {
        Id = existing.Id,
        ContentType = existing.ContentType,
        Data = data is Dictionary<string, object> bag ? new(bag, bag.Comparer) : new(data),
        Status = existing.Status,
        Sensitivity = existing.Sensitivity,
        CreatedAt = existing.CreatedAt,
        UpdatedAt = existing.UpdatedAt,
        ScheduledPublishAt = existing.ScheduledPublishAt,
        ScheduledUnpublishAt = existing.ScheduledUnpublishAt,
        ScheduledSensitivity = existing.ScheduledSensitivity,
        ScheduledSensitivityAt = existing.ScheduledSensitivityAt,
        LastModifiedBy = existing.LastModifiedBy,
        LifecycleState = existing.LifecycleState,
        CreatedBy = existing.CreatedBy,
        SearchText = existing.SearchText,
    };
}
