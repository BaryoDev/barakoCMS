using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action that moves an entry through a named lifecycle transition.
/// </summary>
/// <remarks>
/// The move goes through <see cref="IContentTransitioner"/>, so it faces what the HTTP route
/// faces: the type's transition rules, the fields the transition requires, validation, before-save
/// hooks, the from-state check and the version check.
///
/// It runs as the system and sets <c>SkipPermissionChecks</c>, so the transition permission, the
/// read floor and field sensitivity on the values it sends are not asked. No user stands behind a
/// run, and the workflow's author chose the move. The audit row names the system actor and says the
/// permission checks were skipped.
///
/// The workflow names the type it moves in <c>ContentType</c>. The target id is usually templated
/// from the entry that triggered the run, so a user who can edit that entry could point the field at
/// any entry in the tenant. The target is refused when its type is not the named one, and, when the
/// triggering entry has a branch, when it belongs to another branch.
///
/// Retry safe. An entry already in the transition's end state is reported as done and not moved
/// again, so a rerun after the move committed but before the attempt was recorded changes nothing.
/// A transition that ends where it starts is the one case this cannot tell, and it is always moved.
/// A refusal by a rule, validation or a missing field is permanent. A conflict is retryable, since
/// another writer may finish and leave the entry in the state the move starts from.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Moves an entry through a named lifecycle transition.",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "Transition", "ContentType" },
    OptionalParameters = new[] { "Target", "Data.<field>" },
    ExampleJson = @"{""Type"":""Transition"",""Parameters"":{""Target"":""{{actions.0.id}}"",""ContentType"":""Ticket"",""Transition"":""Request""}}")]
internal sealed class TransitionAction(IDocumentSession session, IContentTransitioner transitioner) : IWorkflowAction
{
    public string Type => "Transition";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        if (!parameters.TryGetValue("Transition", out var name) || string.IsNullOrWhiteSpace(name))
            return WorkflowActionResult.PermanentFailure("Transition is required.");

        if (!parameters.TryGetValue("ContentType", out var type) || string.IsNullOrWhiteSpace(type))
            return WorkflowActionResult.PermanentFailure("ContentType is required.");

        var target = content;
        var raw = parameters.GetValueOrDefault("Target", "subject");
        if (!string.Equals(raw, "subject", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(raw, out var id))
                return WorkflowActionResult.PermanentFailure("Target is not an id.");
            target = await session.LoadAsync<barakoCMS.Models.Content>(id, ct);
            if (target is null)
                return WorkflowActionResult.PermanentFailure("Target does not exist.");
        }

        if (!string.Equals(target.ContentType, type, StringComparison.OrdinalIgnoreCase))
            return WorkflowActionResult.PermanentFailure($"Target is not a '{type}' entry.");
        // Both null counts as the same branch. A run on an entry with no branch moves only entries
        // with no branch, so a trigger outside every branch cannot reach into one.
        if (target.BranchId != content.BranchId)
            return WorkflowActionResult.PermanentFailure("Target belongs to another branch.");

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstNamedAsync(target.ContentType, ct);
        var declared = definition?.Lifecycle?.Transitions
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
            return WorkflowActionResult.PermanentFailure($"'{target.ContentType}' declares no transition named '{name}'.");

        // Already where the move ends: a rerun of an attempt whose move committed.
        if (!string.Equals(declared.From, declared.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.LifecycleState, declared.To, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowActionResult.Success(Outputs(target, declared.To, declared.To, skipped: true));
        }

        var data = parameters
            .Where(kv => kv.Key.StartsWith("Data.", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[5..], kv => (object)kv.Value);

        // A json field takes only a whole list or object, which a template fills as JSON text. A
        // value mixed into other text could add keys, so that is refused. Permanent, no value named.
        var refusal = barakoCMS.Core.Validation.JsonTextFields.Read(
            definition?.Fields, data, key => ActionParameters.IsStructured(parameters, "Data." + key));
        if (refusal is not null)
            return WorkflowActionResult.PermanentFailure(refusal);

        var result = await transitioner.TransitionAsync(
            target, name, ContentTransitionActor.ForSystem("workflow"),
            new ContentTransitionOptions { Data = data, SkipPermissionChecks = true }, ct);

        var message = result.Errors.FirstOrDefault() ?? "The transition was refused.";
        return result.Outcome switch
        {
            ContentTransitionOutcome.Transitioned => WorkflowActionResult.Success(
                Outputs(target, result.FromState, result.ToState, skipped: false)),
            ContentTransitionOutcome.Conflict => WorkflowActionResult.Failure(message),
            _ => WorkflowActionResult.PermanentFailure(message),
        };
    }

    private static Dictionary<string, string> Outputs(barakoCMS.Models.Content target, string? from, string? to, bool skipped) => new()
    {
        ["id"] = target.Id.ToString(),
        ["from"] = from ?? "",
        ["to"] = to ?? "",
        ["skipped"] = skipped ? "true" : "false",
    };
}
