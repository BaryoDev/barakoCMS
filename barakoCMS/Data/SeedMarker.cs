using JasperFx;

namespace barakoCMS.Data;

/// <summary>
/// Records that a one-time step of the data seeder already ran in this deployment, so the next start
/// does not run it again.
/// </summary>
/// <remarks>
/// For a step that must not undo an administrator's later choice, such as granting a capability the
/// administrator then removes. Single-tenanted, like the roles those steps change.
/// </remarks>
internal sealed class SeedMarker
{
    /// <summary>The name of the step.</summary>
    [Identity]
    public string Key { get; set; } = string.Empty;

    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
}
