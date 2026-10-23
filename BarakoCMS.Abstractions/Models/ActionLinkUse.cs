using JasperFx;

namespace barakoCMS.Models;

/// <summary>
/// Marks one signed action link as spent. The link carries a random id, and the first confirmation
/// stores it in the same commit as the transition, so a second use finds the mark and does nothing.
/// </summary>
public class ActionLinkUse
{
    /// <summary>The id inside the link's token.</summary>
    [Identity]
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset ConsumedAt { get; set; }

    /// <summary>When the link would have expired, so a purge can drop the mark afterwards.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
