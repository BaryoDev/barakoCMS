using barakoCMS.Models;

namespace barakoCMS.Features.Content.Runs;

/// <param name="Ordinal">Where the action sits in its run, from 0.</param>
/// <param name="ActionType">The action's type name.</param>
/// <param name="Status">The name of an <see cref="AttemptStatus"/>.</param>
/// <param name="Error">Why the attempt failed, or null.</param>
/// <param name="Created">The entries the action created, or null when it created none.</param>
internal sealed record RunActionSummary(int Ordinal, string ActionType, string Status, string? Error, List<CreatedRef>? Created);

/// <param name="Id">The run's id.</param>
/// <param name="WorkflowName">The workflow's name when it fired.</param>
/// <param name="TriggerEvent">The event that fired it.</param>
/// <param name="Status">The name of a <see cref="RunStatus"/>.</param>
/// <param name="CreatedAt">When the run was queued, in UTC.</param>
/// <param name="CompletedAt">When it finished, or null while it has not.</param>
/// <param name="Actions">Its actions in order.</param>
internal sealed record RunSummary(
    Guid Id,
    string WorkflowName,
    string TriggerEvent,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    List<RunActionSummary> Actions);

internal sealed record RunsResponse(List<RunSummary> Runs);
