using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Interface for workflow schema validation.
/// </summary>
public interface IWorkflowSchemaValidator
{
    /// <summary>
    /// Validate a workflow definition.
    /// </summary>
    /// <param name="workflow">The workflow definition to validate.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Validation result with any errors found.</returns>
    WorkflowValidationResult Validate(WorkflowDefinition workflow, CancellationToken ct = default);

    /// <summary>
    /// Validate a workflow definition, including the checks that need to read the triggering
    /// content type.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Validate"/> because a trigger naming a lifecycle transition can only
    /// be checked against the type that declares it, and that is a database read. The default
    /// implementation exists so an existing implementor still compiles; it skips the lifecycle check,
    /// so anything that saves a workflow calls this and not <see cref="Validate"/>.
    /// </remarks>
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken ct = default)
        => Task.FromResult(Validate(workflow, ct));
}

/// <summary>
/// Validates workflow definitions against schema and business rules.
/// </summary>
public class WorkflowSchemaValidator : IWorkflowSchemaValidator
{
    private readonly IWorkflowPluginRegistry _pluginRegistry;
    private readonly IQuerySession _session;
    private readonly bool _allowInsecureSignedUrls;

    public WorkflowSchemaValidator(IWorkflowPluginRegistry pluginRegistry, IQuerySession session)
        : this(pluginRegistry, session, configuration: null)
    {
    }

    public WorkflowSchemaValidator(IWorkflowPluginRegistry pluginRegistry, IQuerySession session, IConfiguration? configuration)
    {
        _pluginRegistry = pluginRegistry;
        _session = session;
        _allowInsecureSignedUrls = WebhookSigning.AllowsInsecureSignedUrls(configuration);
    }

    public WorkflowValidationResult Validate(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new WorkflowValidationResult { IsValid = true };

        // Validate basic fields
        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "name",
                Message = "Workflow name is required"
            });
            result.IsValid = false;
        }

        // TriggerContentType and TriggerContentTypes together name the types, so either one is
        // enough. A blank entry in the list is refused rather than skipped: it is a type the caller
        // meant to name and did not, and skipping it saves a workflow that never fires for it.
        var listed = workflow.TriggerContentTypes ?? [];
        for (var i = 0; i < listed.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(listed[i]))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = $"triggerContentTypes[{i}]",
                    Message = "A trigger content type cannot be blank"
                });
                result.IsValid = false;
            }
        }

        if (WorkflowTriggers.ContentTypes(workflow).Count == 0 && !listed.Any(string.IsNullOrWhiteSpace))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerContentType",
                Message = "Trigger content type is required"
            });
            result.IsValid = false;
        }

        // The same reading as the content types: TriggerEvent and TriggerEvents together name the
        // events, either one is enough, and a blank or unknown entry in the list is refused.
        var listedEvents = workflow.TriggerEvents ?? [];
        for (var i = 0; i < listedEvents.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(listedEvents[i]))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = $"triggerEvents[{i}]",
                    Message = "A trigger event cannot be blank"
                });
                result.IsValid = false;
            }
            else if (!WorkflowEvents.IsValid(listedEvents[i]))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = $"triggerEvents[{i}]",
                    Message = $"Trigger event must be one of: {string.Join(", ", WorkflowEvents.All)}"
                });
                result.IsValid = false;
            }
        }

        if (WorkflowTriggers.Events(workflow).Count == 0 && !listedEvents.Any(string.IsNullOrWhiteSpace))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerEvent",
                Message = "Trigger event is required"
            });
            result.IsValid = false;
        }

        // Validate trigger event is a known value
        if (!string.IsNullOrWhiteSpace(workflow.TriggerEvent) && !WorkflowEvents.IsValid(workflow.TriggerEvent))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerEvent",
                Message = $"Trigger event must be one of: {string.Join(", ", WorkflowEvents.All)}"
            });
            result.IsValid = false;
        }

        // Validate actions
        if (workflow.Actions == null || workflow.Actions.Count == 0)
        {
            result.Errors.Add(new ValidationError
            {
                Field = "actions",
                Message = "At least one action is required"
            });
            result.IsValid = false;
        }
        else
        {
            for (int i = 0; i < workflow.Actions.Count; i++)
            {
                ValidateAction(workflow.Actions[i], i, result);
            }

            var onTransition = WorkflowTriggers.Events(workflow).Any(WorkflowEvents.IsTransition);
            for (int i = 0; i < workflow.Actions.Count; i++)
            {
                AddPlaceholderWarnings(workflow.Actions[i], $"actions[{i}]", onTransition, result, depth: 0);
            }
        }

        return result;
    }

    /// <summary>How many placeholder warnings one validation lists. The last entry says when there are more.</summary>
    internal const int MaxPlaceholderWarnings = 50;

    /// <summary>How far into nested Conditional branches the placeholders are read.</summary>
    internal const int MaxWarningDepth = 5;

    /// <summary>
    /// Lists every placeholder of an action that the engine will send as written. Never an error:
    /// a template has always been allowed to hold text the engine does not know.
    /// </summary>
    /// <remarks>
    /// A Conditional's condition is skipped, since the action reads it itself, and its branches are
    /// read as the child actions they hold. A branch that is not such a list is passed over here;
    /// the response already names it as unreadable.
    /// </remarks>
    private static void AddPlaceholderWarnings(
        WorkflowAction action, string field, bool onTransition, WorkflowValidationResult result, int depth)
    {
        if (action.Parameters is null) return;

        foreach (var (name, template) in action.Parameters)
        {
            if (result.Warnings.Count > MaxPlaceholderWarnings) return;

            if (ActionParameters.IsResolvedByTheAction(action.Type ?? string.Empty, name))
            {
                if (depth < MaxWarningDepth && !name.Equals("Condition", StringComparison.OrdinalIgnoreCase))
                {
                    var children = ChildActions(template);
                    for (var i = 0; i < children.Count; i++)
                    {
                        AddPlaceholderWarnings(children[i], $"{field}.parameters.{name}[{i}]", onTransition, result, depth + 1);
                    }
                }

                continue;
            }

            foreach (var message in PlaceholderWarnings(action.Type, name, template, onTransition))
            {
                if (result.Warnings.Count == MaxPlaceholderWarnings)
                {
                    result.Warnings.Add(new ValidationError
                    {
                        Field = "actions",
                        Message = $"More than {MaxPlaceholderWarnings} placeholder warnings. Only the first {MaxPlaceholderWarnings} are listed"
                    });
                    return;
                }

                result.Warnings.Add(new ValidationError { Field = $"{field}.parameters.{name}", Message = message });
            }
        }
    }

    /// <summary>The warnings for one parameter of one action.</summary>
    /// <remarks>
    /// A warning quotes the placeholder it is about, so a parameter the responses leave out as a
    /// credential (<see cref="WebhookSigning.IsSensitiveParameterName"/>) is counted and not quoted.
    ///
    /// UpdateField and CreateTask write their parameters into an entry. A user's address written
    /// there is served by delivery when the field is public on a deliverable type, so it is said at
    /// save. Said and not refused: which entry and field the action writes can itself be a
    /// placeholder, so the save cannot tell a public field from a private one.
    /// </remarks>
    private static IEnumerable<string> PlaceholderWarnings(string? actionType, string name, string? template, bool onTransition)
    {
        if (WebhookSigning.IsSensitiveParameterName(name))
        {
            var count = TemplateExpression.Problems(template, onTransition).Count();
            if (count > 0)
            {
                yield return $"This parameter holds {count} placeholder(s) that will be sent as written. "
                           + "It is a credential, so its text is not shown here.";
            }

            yield break;
        }

        foreach (var problem in TemplateExpression.Problems(template, onTransition))
        {
            yield return problem;
        }

        if (actionType is "UpdateField" or "CreateTask" && TemplateExpression.NamesAddress(template))
        {
            yield return "This writes a user's email address into an entry. "
                       + "If the field it lands in is public on a deliverable content type, delivery serves the address.";
        }
    }

    private static List<WorkflowAction> ChildActions(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return [];

        try
        {
            var children = System.Text.Json.JsonSerializer.Deserialize<List<WorkflowAction>>(branch) ?? [];
            return children.Where(child => child is not null).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Everything <see cref="Validate"/> checks, plus that a trigger naming a transition names one
    /// the triggering content type actually declares.
    /// </summary>
    /// <remarks>
    /// A workflow that names an undeclared transition saves happily and then never fires, and a
    /// workflow that never fires looks identical to one that fires and fails. Refusing it here is
    /// the only moment that is cheap to correct.
    ///
    /// This also settles the casing. The engine matches TriggerEvent with an equality query, while
    /// the lifecycle matches a transition name case insensitively, so "transition:approve" against a
    /// transition declared "Approve" would validate and then never match an event. The declared
    /// spelling is handed back in <see cref="WorkflowValidationResult.NormalisedTriggerEvent"/> for
    /// the caller to store.
    /// </remarks>
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        var result = Validate(workflow, ct);

        var events = WorkflowTriggers.Events(workflow);
        if (!events.Any(e => WorkflowEvents.TransitionName(e) is { Length: > 0 }))
        {
            return result;
        }

        // In the order Events gives them, so the first is TriggerEvent whenever that field is set.
        var normalised = new List<string>(events.Count);
        var transitionsValid = true;

        foreach (var triggerEvent in events)
        {
            var transition = WorkflowEvents.TransitionName(triggerEvent);
            if (transition is null or { Length: 0 })
            {
                normalised.Add(triggerEvent);
                continue;
            }

            // The single field when the trigger came from it, the list entry otherwise, so a
            // request that sent only the list is not told about a field it never sent.
            var field = string.Equals(triggerEvent, workflow.TriggerEvent, StringComparison.Ordinal)
                ? "triggerEvent"
                : $"triggerEvents[{(workflow.TriggerEvents ?? []).IndexOf(triggerEvent)}]";

            var (valid, spelling) = await DeclaredSpellingAsync(workflow, transition, field, result, ct);
            transitionsValid &= valid;
            normalised.Add(spelling is null ? triggerEvent : WorkflowEvents.ForTransition(spelling));
        }

        if (!transitionsValid)
        {
            result.IsValid = false;
            return result;
        }

        result.NormalisedTriggerEvents = normalised.Distinct(StringComparer.Ordinal).ToList();

        if (WorkflowEvents.TransitionName(workflow.TriggerEvent) is { Length: > 0 }
            && WorkflowEvents.IsTransition(normalised[0]))
        {
            result.NormalisedTriggerEvent = normalised[0];
        }

        return result;
    }

    /// <summary>
    /// Checks one transition against every content type the workflow names, adding an error for each
    /// type that does not declare it.
    /// </summary>
    /// <returns>
    /// Whether every type declares it, and the declared spelling. The spelling is null when the
    /// workflow names no type, which <see cref="Validate"/> has already refused.
    /// </returns>
    private async Task<(bool Valid, string? Spelling)> DeclaredSpellingAsync(
        WorkflowDefinition workflow, string transition, string field, WorkflowValidationResult result, CancellationToken ct)
    {
        // Every named type is checked, and every failure reported, so a list with two mistakes is
        // not fixed one save at a time.
        string? spelling = null;
        string? spelledBy = null;
        var transitionValid = true;

        foreach (var contentType in WorkflowTriggers.ContentTypes(workflow))
        {
            var definition = await _session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(d => d.Name == contentType, ct);

            // A missing type is refused rather than passed over. Skipping the check when the thing to
            // check against is absent is how a validation quietly stops validating, and here it would
            // let through exactly the workflow that never fires.
            if (definition is null)
            {
                result.Errors.Add(new ValidationError
                {
                    Field = field,
                    Message = $"Content type '{contentType}' does not exist, so its transitions cannot be checked",
                });
                transitionValid = false;
                continue;
            }

            var declared = definition.Lifecycle?.Transitions ?? new List<StateTransition>();
            var match = declared.FirstOrDefault(t => string.Equals(t.Name, transition, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var available = declared.Count == 0
                    ? "(none)"
                    : string.Join(", ", declared.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

                result.Errors.Add(new ValidationError
                {
                    Field = field,
                    Message = $"'{transition}' is not a transition on '{contentType}'. Declared transitions: {available}",
                });
                transitionValid = false;
                continue;
            }

            // A trigger is stored once and matched by equality, so the types have to agree on how
            // the transition is spelled, or the workflow would fire for some of them and not others.
            if (spelling is null)
            {
                spelling = match.Name;
                spelledBy = contentType;
            }
            else if (!string.Equals(spelling, match.Name, StringComparison.Ordinal))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = field,
                    Message = $"'{spelledBy}' declares the transition as '{spelling}' and '{contentType}' as '{match.Name}'. "
                            + "One workflow matches one spelling, so use a workflow per spelling",
                });
                transitionValid = false;
            }
        }

        return (transitionValid, spelling);
    }

    private static readonly string[] ConditionalBranches = ["ThenActions", "ElseActions"];

    /// <summary>
    /// Refuses an onFailure on a child of a Conditional, in either branch.
    /// </summary>
    /// <remarks>
    /// A child has no policy: the Conditional fails or succeeds as one action. Accepted and
    /// ignored, a chain written inside a branch would look as though it stops and would not.
    ///
    /// Any casing of the type, the branch name and the key, the way the branches are matched where
    /// their credentials are handled. A branch that is not a JSON list is left to the checks that
    /// read branches, and never runs.
    /// </remarks>
    private static void RefuseChildPolicies(WorkflowAction action, string fieldPrefix, WorkflowValidationResult result)
    {
        if (action.Parameters is null) return;
        if (!string.Equals(action.Type, "Conditional", StringComparison.OrdinalIgnoreCase)) return;

        foreach (var (name, json) in action.Parameters)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            if (!ConditionalBranches.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                using var branch = System.Text.Json.JsonDocument.Parse(json);
                if (branch.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

                var child = 0;
                foreach (var element in branch.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                        && element.EnumerateObject().Any(p => string.Equals(p.Name, "onFailure", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Errors.Add(new ValidationError
                        {
                            Field = $"{fieldPrefix}.parameters.{name}[{child}].onFailure",
                            Message = "An action inside a Conditional has no onFailure. Set it on the Conditional, which fails or succeeds as one action"
                        });
                        result.IsValid = false;
                    }

                    child++;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON, so there is no child to look at. The action refuses the branch when it runs.
            }
        }
    }

    private void ValidateAction(WorkflowAction action, int index, WorkflowValidationResult result)
    {
        var fieldPrefix = $"actions[{index}]";

        // Check if action type is registered
        if (string.IsNullOrWhiteSpace(action.Type))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.type",
                Message = "Action type is required"
            });
            result.IsValid = false;
            return;
        }

        if (!_pluginRegistry.IsActionRegistered(action.Type))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.type",
                Message = $"Unknown action type '{action.Type}'. Available types: {string.Join(", ", _pluginRegistry.GetAllActions().Select(a => a.Type))}"
            });
            result.IsValid = false;
            return;
        }

        // A number is accepted wherever a name is, so a value that names no policy can arrive. It
        // would be stored and then read as neither, which here means the chain is not stopped.
        if (action.OnFailure is { } policy && !Enum.IsDefined(policy))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.onFailure",
                Message = $"onFailure must be one of: {string.Join(", ", Enum.GetNames<WorkflowFailurePolicy>())}"
            });
            result.IsValid = false;
        }

        RefuseChildPolicies(action, fieldPrefix, result);

        // Validate required parameters
        var metadata = _pluginRegistry.GetActionMetadata(action.Type);
        if (metadata != null && metadata.RequiredParameters.Any())
        {
            foreach (var requiredParam in metadata.RequiredParameters)
            {
                if (!action.Parameters.ContainsKey(requiredParam) ||
                    string.IsNullOrWhiteSpace(action.Parameters[requiredParam]))
                {
                    result.Errors.Add(new ValidationError
                    {
                        Field = $"{fieldPrefix}.parameters.{requiredParam}",
                        Message = $"Required parameter '{requiredParam}' is missing or empty"
                    });
                    result.IsValid = false;
                }
            }
        }

        if (string.Equals(action.Type, "Webhook", StringComparison.Ordinal)
            && WebhookSigning.IsInsecureSignedUrl(action.Parameters.GetValueOrDefault("Url"), action.Parameters, _allowInsecureSignedUrls))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.parameters.Url",
                Message = WebhookSigning.InsecureSignedUrlReason
            });
            result.IsValid = false;
        }
    }
}
