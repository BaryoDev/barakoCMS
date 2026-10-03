using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetUniqueness;

internal class Request
{
    /// <summary>
    /// Every rule the type declares from now on. A rule left out is removed, and an empty list or
    /// null removes them all.
    /// </summary>
    public List<UniquenessRule>? Uniqueness { get; set; }

    /// <summary>
    /// Required when entries already share their values under a rule this request adds or changes.
    /// Ignored otherwise.
    /// </summary>
    /// <remarks>
    /// No entry is changed. Those entries keep their values and stay writable; a write that would
    /// bring another entry to values one of them holds is refused. The refusal counts them, and
    /// <c>GET /api/content-types/{name}/uniqueness/{rule}/duplicates</c> lists them.
    /// </remarks>
    public bool Force { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The rules the type now declares, or null when it has none.</summary>
    public List<UniquenessRule>? Uniqueness { get; set; }

    /// <summary>
    /// For each rule this request added or changed, how many entries already share their values
    /// with another entry the rule counts. Empty when none do or nothing was added.
    /// </summary>
    public List<RuleDuplicates> Duplicates { get; set; } = new();
}

internal class RuleDuplicates
{
    public string Rule { get; set; } = string.Empty;

    public int Entries { get; set; }
}
