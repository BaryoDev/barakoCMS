using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetFieldOptions;

internal class Request
{
    /// <summary>Every option the field accepts from now on, in display order.</summary>
    public List<FieldOption> Options { get; set; } = new();

    /// <summary>
    /// Required to remove an option that entries still hold, and ignored otherwise.
    /// </summary>
    /// <remarks>
    /// Removing an option in use leaves those entries holding a value the field no longer accepts.
    /// They are not rewritten, and each one is refused on its next save until somebody picks a value
    /// that is still offered. The refusal names how many entries that is, which is the number worth
    /// seeing before the second attempt.
    /// </remarks>
    public bool Force { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public List<FieldOption> Options { get; set; } = new();

    /// <summary>Values that were options before this call and are not now.</summary>
    public List<string> Removed { get; set; } = new();

    /// <summary>How many entries hold at least one removed value. Zero unless force was set.</summary>
    public int EntriesHoldingRemoved { get; set; }
}
