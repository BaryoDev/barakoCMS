using JasperFx;

namespace barakoCMS.Models;

/// <summary>
/// Records that a one-time step of the data seeder already ran in a tenant, so the next start does
/// not run it again.
/// </summary>
/// <remarks>
/// For a step that must not undo an administrator's later choice, such as granting a capability the
/// administrator then removes.
/// </remarks>
public class SeedMarker
{
    /// <summary>The name of the step.</summary>
    [Identity]
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
}
