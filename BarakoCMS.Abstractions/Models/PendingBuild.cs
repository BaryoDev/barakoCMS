namespace barakoCMS.Models;

/// <summary>
/// A build trigger waiting for its quiet period to end. One row per build hook target and tenant:
/// the first publish in a quiet period writes it, every later publish finds it and does nothing, and
/// the sweep fires the hook once at <see cref="FireAt"/> and removes the row.
/// </summary>
public class PendingBuild
{
    /// <summary>A hash of the workflow and the target, so one target has at most one pending row.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The tenant the publish happened in, as the slug the workflow runs under.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The same key as <see cref="Id"/>, kept as a field so it reads in a query.</summary>
    public string ActionKey { get; set; } = string.Empty;

    /// <summary>When the build fires: the first publish plus the quiet period.</summary>
    public DateTimeOffset FireAt { get; set; }

    /// <summary>Set by the instance that took the row. A claim older than a few minutes is taken over.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>The action's parameters as the workflow holds them, the secret still encrypted.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
}
