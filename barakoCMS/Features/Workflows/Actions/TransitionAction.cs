using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Moves the entry in scope through a named transition of its type's lifecycle, the same audited
/// move the change-status endpoint makes. The workflow acts as the system actor <c>workflow</c>
/// on its own authority, since a workflow is authored by someone holding ManageWorkflows. A
/// refused move fails the step.
/// </summary>
[WorkflowActionMetadata(
    Description = "Move the entry through a named lifecycle transition",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "Transition" },
    OptionalParameters = new[] { "Data.<field>" },
    ExampleJson = @"{""Type"":""Transition"",""Parameters"":{""Transition"":""approve""}}"
)]
internal class TransitionAction(
    IContentTransitioner transitioner,
    ILogger<TransitionAction> logger) : IWorkflowAction
{
    internal const string ActorName = "workflow";

    /// <inheritdoc />
    public string Type => "Transition";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var transition = parameters.GetValueOrDefault("Transition");
        if (string.IsNullOrWhiteSpace(transition))
        {
            return WorkflowActionResult.PermanentFailure("Transition needs the name of a transition.");
        }

        var data = parameters
            .Where(p => p.Key.StartsWith("Data.", StringComparison.Ordinal))
            .ToDictionary(p => p.Key["Data.".Length..], p => (object)p.Value);

        var result = await transitioner.TransitionAsync(
            content,
            transition,
            ContentTransitionActor.ForSystem(ActorName),
            new ContentTransitionOptions { Data = data.Count == 0 ? null : data, SkipPermissionChecks = true },
            ct);

        if (result.Succeeded)
        {
            logger.LogInformation(
                "Moved {ContentId} from {From} to {To} by workflow transition {Transition}",
                content.Id, result.FromState, result.ToState, transition);
            return WorkflowActionResult.Success(new Dictionary<string, string>
            {
                ["FromState"] = result.FromState ?? string.Empty,
                ["ToState"] = result.ToState ?? string.Empty,
            });
        }

        var reason = $"The transition '{transition}' was refused: {string.Join("; ", result.Errors)}";

        // A conflict is another writer winning the race and can pass on a retry. The rest are about
        // the entry or the type and will not.
        return result.Outcome == ContentTransitionOutcome.Conflict
            ? WorkflowActionResult.Failure(reason)
            : WorkflowActionResult.PermanentFailure(reason);
    }
}
