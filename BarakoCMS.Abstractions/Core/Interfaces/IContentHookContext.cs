namespace barakoCMS.Core.Interfaces;

/// <summary>
/// What a lifecycle hook is told about the write it is guarding, with no persistence type in it.
/// </summary>
/// <remarks>
/// <see cref="IContentLifecycleHook.BeforeSaveAsync"/> receives this. <see cref="Data"/> is the live
/// dictionary that will be persisted, so a hook may enrich it as well as reject the write. To read
/// other entries use <see cref="Reader"/>.
/// </remarks>
public interface IContentHookContext
{
    string ContentType { get; }

    Dictionary<string, object> Data { get; }

    /// <summary>The entry's existing data on update; null on create.</summary>
    IReadOnlyDictionary<string, object>? Existing { get; }

    /// <summary>The id of the entry being written; null on create.</summary>
    Guid? EntryId { get; }

    Guid UserId { get; }

    bool IsCreate { get; }

    /// <summary>Reads other entries of the tenant the write belongs to.</summary>
    IContentReader Reader { get; }
}
