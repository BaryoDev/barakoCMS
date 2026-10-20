using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

public interface IContentLifecycleRunner
{
    /// <summary>
    /// Runs every <see cref="IContentLifecycleHook"/> registered for <paramref name="contentType"/>,
    /// in registration order, then the parent check for a field the type declares a parent, before
    /// the entry is written. Hooks may enrich <paramref name="data"/>
    /// in place. Returns every error collected; a non-empty result must abort the write.
    ///
    /// <paramref name="entryId"/> is the id of the entry being written, null on create. It has no
    /// default deliberately: an update that left it out would compile and hand every hook a context
    /// that cannot tell which entry it is guarding.
    /// </summary>
    Task<IReadOnlyList<string>> RunBeforeSaveAsync(
        string contentType,
        Guid? entryId,
        Dictionary<string, object> data,
        IReadOnlyDictionary<string, object>? existing,
        Guid userId,
        CancellationToken ct);
}
