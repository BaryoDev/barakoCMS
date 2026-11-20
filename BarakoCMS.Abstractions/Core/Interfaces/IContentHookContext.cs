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

    /// <summary>Reads other entries, across the whole database the write session points at (no tenant filter).</summary>
    IContentReader Reader { get; }

    /// <summary>The lifecycle transition this write is; null for a plain save.</summary>
    ContentTransition? Transition => null;

    /// <summary>
    /// Takes a lock named by <paramref name="key"/> for this tenant, held until the transaction the
    /// content write runs in ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is a Postgres advisory lock scoped to the transaction, taken after the write's
    /// transaction has been opened. It is released when the write commits or rolls back, never
    /// earlier: returning from the hook does not release it. Another write that asks for the same
    /// key in the same tenant waits until then, and sees what this one committed through <see cref="LoadAsync{T}"/> and
    /// <see cref="Reader"/>, not through the existing data, which was loaded before the hooks ran. It is exclusive,
    /// and a wait has no timeout beyond the caller's cancellation token.
    /// </para>
    /// <para>
    /// The key is the hook's to name, and it is combined with the tenant, so the same key in two
    /// tenants is two locks. It is not combined with the module, so start it with the module's own
    /// name to keep it from meeting another module's key. Take locks in one order across hooks, or
    /// two writes can wait on each other.
    /// </para>
    /// </remarks>
    Task LockAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This context does not take locks.");

    /// <summary>
    /// A document this hook stored in this write, or else the committed one, for the tenant of the
    /// write. Null when there is none.
    /// </summary>
    /// <typeparam name="T">A document type with a <see cref="Guid"/> <c>Id</c> that the host stores.</typeparam>
    /// <remarks>
    /// For the hook's own writes: a document read here and changed is stored back with
    /// <see cref="Store{T}"/>, in the same transaction as the content write. It is not a general
    /// document store for a module's other code, so a core document type (a role, a user, a content
    /// type definition) throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken cancellationToken) where T : class =>
        throw new NotSupportedException("This context does not load documents.");

    /// <summary>
    /// Stores a document in the same unit of work as the content write, in the tenant of the write.
    /// It commits with the content write, and is not stored if the write fails or is rolled back.
    /// </summary>
    /// <typeparam name="T">A document type with a <see cref="Guid"/> <c>Id</c> that the host stores.</typeparam>
    /// <remarks>
    /// A <see cref="Guid.Empty"/> id is given a new one. <see cref="LoadAsync{T}"/> in the same
    /// write returns this document.
    /// </remarks>
    void Store<T>(T document) where T : class =>
        throw new NotSupportedException("This context does not store documents.");
}
