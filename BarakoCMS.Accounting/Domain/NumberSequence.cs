namespace BarakoCMS.Accounting.Domain;

/// <summary>
/// A named, monotonically increasing counter (e.g. "JE-2026") used to allocate sequential
/// journal-entry numbers. Registered with Marten optimistic concurrency so two concurrent posts
/// can't hand out the same number — the loser gets a concurrency error and retries.
/// </summary>
public class NumberSequence
{
    public string Id { get; set; } = string.Empty;
    public long Value { get; set; }
}
