namespace BarakoCMS.Accounting.Domain;

/// <summary>
/// A named, monotonically increasing counter (e.g. "JE-2026") used to allocate sequential
/// journal-entry numbers. Registered with Marten optimistic concurrency so two concurrent posts
/// can't hand out the same number — the loser gets a concurrency error and retries.
/// </summary>
/// <remarks>
/// Journal entries are numbered by the core gapless allocator from 5.0 (see
/// <c>JournalEntryHook.EntryNumbers</c>); these rows are read only by the obsolete parameterless
/// hook, and kept so a database that holds them, and a rollback to 4.9, can still read them.
/// </remarks>
public class NumberSequence
{
    public string Id { get; set; } = string.Empty;
    public long Value { get; set; }
}
