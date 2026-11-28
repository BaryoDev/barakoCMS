using Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// The type lock for writers outside the core assembly, such as the <c>contents</c> section of a
/// config apply, which writes entries of several types.
/// </summary>
public static class ContentTypeLocks
{
    /// <summary>
    /// Takes each type's lock shared, lowest lowered name first, held until the session's
    /// transaction ends. Call it before staging, so a change to the types cannot wait on a lock
    /// the write holds while the write waits on another.
    /// </summary>
    public static Task LockSharedAsync(IDocumentSession session, IEnumerable<string> contentTypes, CancellationToken ct) =>
        FieldChanges.LockTypesSharedAsync(session, contentTypes, ct);
}
