using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetLifecycle;

internal sealed class Request
{
    /// <summary>
    /// The lifecycle the type declares from now on: its states, its initial state and its
    /// transitions, each with the fields it requires or takes. It replaces the stored one whole.
    /// </summary>
    public LifecycleDefinition? Lifecycle { get; set; }

    /// <summary>
    /// Run every check the real edit runs and answer 200 with what would block it, storing nothing.
    /// </summary>
    public bool DryRun { get; set; }
}

internal sealed class Response : barakoCMS.Core.Validation.IDryRunResponse
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The type's version after this call, the same value as the <c>ETag</c> header. Send it as
    /// <c>If-Match</c> on the next edit.
    /// </summary>
    public Guid Version { get; init; }

    /// <summary>
    /// The lifecycle the type declares now. For a dry run, the one it would declare if
    /// <see cref="CanApply"/> is true.
    /// </summary>
    public LifecycleDefinition Lifecycle { get; init; } = new();

    /// <summary>True when this answers a dry run: nothing was stored.</summary>
    public bool DryRun { get; init; }

    /// <summary>False when the proposed lifecycle is the one already stored: nothing was written.</summary>
    public bool Changed { get; init; }

    /// <summary>For a dry run, whether the real edit would be accepted. True otherwise.</summary>
    public bool CanApply { get; init; } = true;

    /// <summary>
    /// Everything that would refuse the edit, every kind in one list. Empty on a stored edit. A dry
    /// run lists every blocker, not the first.
    /// </summary>
    public IReadOnlyList<LifecycleBlocker> Blockers { get; init; } = [];

    /// <summary>What the edit leaves dangling without refusing it, such as a template link.</summary>
    public IReadOnlyList<LifecycleBlocker> Warnings { get; init; } = [];
}

/// <summary>The 409 body of a refused edit.</summary>
/// <remarks>
/// <c>status</c>, <c>title</c> and <c>errors</c> have the shape every other 409 of the API has, so a
/// client that reads <c>errors[].reason</c> shows the same sentences. <c>blockers</c> and
/// <c>warnings</c> are the same findings as data, one object each, in the shape a dry run answers.
/// </remarks>
internal sealed class Refusal
{
    public int Status { get; init; } = 409;

    public string Title { get; init; } = "Conflict";

    public List<RefusalError> Errors { get; init; } = [];

    public IReadOnlyList<LifecycleBlocker> Blockers { get; init; } = [];

    public IReadOnlyList<LifecycleBlocker> Warnings { get; init; } = [];
}

/// <summary>The 412 body of a save that started from a version that is no longer current.</summary>
/// <remarks>Carries what the caller needs to reload: the current lifecycle and the current version.</remarks>
internal sealed class StaleRefusal
{
    public int Status { get; init; } = 412;

    public string Title { get; init; } = "Precondition Failed";

    public List<RefusalError> Errors { get; init; } = [];

    /// <summary>The lifecycle stored now, or null when the type has none.</summary>
    public LifecycleDefinition? Lifecycle { get; init; }

    /// <summary>The version stored now, also sent as the <c>ETag</c> header.</summary>
    public Guid Version { get; init; }
}

internal sealed record RefusalError(string Name, string Reason);
