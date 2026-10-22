using System.Security.Claims;
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// What the user saving a workflow must hold for the content actions it carries.
/// </summary>
/// <remarks>
/// CreateContent, CreateTask, UpdateField and Transition run as the system, with the permission checks skipped. Saving a
/// workflow is the one moment a person chooses what they do, so that person is asked then: they
/// need create on each type a CreateContent or CreateTask action names, update on each type an
/// UpdateField action writes, and the transition right on each type
/// and transition a Transition action names. Without this, the ManageWorkflows capability would be
/// a way to write and move entries a role was never given. Actions inside a Conditional's branches
/// and compensations are read too.
///
/// A parameter that is a template (it holds <c>{{</c>) cannot be named at save time. For a
/// Transition that is refused, since the right is on a type and a name. The ContentType of a
/// Transition is required and checked on every run, so it is normally plain text.
/// </remarks>
internal static class WorkflowSaveRights
{
    private static readonly string[] Branches = ["ThenActions", "ElseActions"];

    /// <summary>The rights the user lacks, each said in a sentence. Empty when they hold them all.</summary>
    public static async Task<List<string>> MissingAsync(
        IDocumentSession session, IPermissionResolver permissions, IWorkflowPluginRegistry registry,
        ClaimsPrincipal principal, WorkflowDefinition workflow, CancellationToken ct)
    {
        var needs = Needs(workflow).ToList();
        var capabilities = CapabilityNeeds(registry, workflow).ToList();
        var missing = new List<string>();
        if (needs.Count == 0 && capabilities.Count == 0) return missing;

        User? user = null;
        if (Guid.TryParse(principal.FindFirst("UserId")?.Value, out var id))
            user = await session.LoadAsync<User>(id, ct);

        foreach (var (type, action, label) in needs.Distinct())
        {
            if (user is null || string.IsNullOrWhiteSpace(type) || type.Contains("{{") || action.Contains("{{")
                || !await permissions.CanPerformActionAsync(user, type, action, null, ct))
            {
                missing.Add(label);
            }
        }

        foreach (var (capability, label) in capabilities.Distinct())
        {
            if (user is null || !await permissions.HasCapabilityAsync(user.Id, capability, ct))
                missing.Add(label);
        }

        return missing;
    }

    /// <summary>
    /// An action can name a capability in its metadata. Saving a workflow that carries it asks the
    /// saver for that capability, since the action runs as the system and checks no one at run time.
    /// </summary>
    private static IEnumerable<(string Capability, string Label)> CapabilityNeeds(
        IWorkflowPluginRegistry registry, WorkflowDefinition workflow)
    {
        foreach (var action in Flatten(workflow))
        {
            var capability = registry.GetActionMetadata(action.Type)?.RequiredCapability;
            if (!string.IsNullOrWhiteSpace(capability))
                yield return (capability, $"You do not hold the '{capability}' capability, so you cannot save a workflow with a {action.Type} action.");
        }
    }

    private static IEnumerable<(string Type, string Action, string Label)> Needs(WorkflowDefinition workflow)
    {
        foreach (var action in Flatten(workflow))
        {
            var type = action.Parameters.GetValueOrDefault("ContentType")?.Trim() ?? "";
            if (string.Equals(action.Type, "CreateContent", StringComparison.OrdinalIgnoreCase))
            {
                yield return (type, "create", $"You cannot create '{type}' entries, so you cannot save a workflow that does.");
            }
            else if (string.Equals(action.Type, "CreateTask", StringComparison.OrdinalIgnoreCase))
            {
                // The action defaults to a "Task" type when none is given.
                var created = action.Parameters.TryGetValue("ContentType", out var named) ? named.Trim() : "Task";
                yield return (created, "create", $"You cannot create '{created}' entries, so you cannot save a workflow that does.");
            }
            else if (string.Equals(action.Type, "UpdateField", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var need in UpdateNeeds(workflow, action, type)) yield return need;
            }
            else if (string.Equals(action.Type, "Transition", StringComparison.OrdinalIgnoreCase))
            {
                var name = action.Parameters.GetValueOrDefault("Transition")?.Trim() ?? "";
                yield return (type, PermissionResolver.TransitionActionPrefix + name,
                    $"You cannot make the '{name}' transition on '{type}' entries, so you cannot save a workflow that does.");
            }
        }
    }

    /// <summary>
    /// With no TargetId, UpdateField writes the entry that fired the workflow, so the user needs
    /// update on each type the workflow triggers on. With a TargetId the type of the target cannot
    /// be known at save time, so the action has to name it in ContentType (the run refuses a target
    /// of another type) and the user needs update on that.
    /// </summary>
    private static IEnumerable<(string Type, string Action, string Label)> UpdateNeeds(
        WorkflowDefinition workflow, WorkflowAction action, string named)
    {
        if (string.IsNullOrWhiteSpace(action.Parameters.GetValueOrDefault("TargetId")))
        {
            var triggers = workflow.TriggerContentTypes.Append(workflow.TriggerContentType)
                .Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (triggers.Count == 0) triggers.Add("");
            foreach (var trigger in triggers)
                yield return (trigger, "update", $"You cannot update '{trigger}' entries, so you cannot save a workflow that does.");
            yield break;
        }

        yield return (named, "update", string.IsNullOrWhiteSpace(named)
            ? "An UpdateField action with a TargetId must name the ContentType it updates."
            : $"You cannot update '{named}' entries, so you cannot save a workflow that does.");
    }

    private static IEnumerable<WorkflowAction> Flatten(WorkflowDefinition workflow)
    {
        foreach (var action in workflow.Actions)
        {
            foreach (var each in Expand(action)) yield return each;
            if (action.Compensate is { } undo)
                yield return new WorkflowAction { Type = undo.Type, Parameters = undo.Parameters };
        }
    }

    private static IEnumerable<WorkflowAction> Expand(WorkflowAction action)
    {
        yield return action;
        if (!string.Equals(action.Type, "Conditional", StringComparison.OrdinalIgnoreCase)) yield break;

        foreach (var (name, json) in action.Parameters)
        {
            if (!Branches.Contains(name, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(json)) continue;
            foreach (var child in Children(json))
                foreach (var each in Expand(child)) yield return each;
        }
    }

    private static List<WorkflowAction> Children(string json)
    {
        var found = new List<WorkflowAction>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var child = new WorkflowAction();
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "Type", StringComparison.OrdinalIgnoreCase))
                        child.Type = property.Value.GetString() ?? "";
                    else if (string.Equals(property.Name, "Parameters", StringComparison.OrdinalIgnoreCase)
                             && property.Value.ValueKind == JsonValueKind.Object)
                        foreach (var p in property.Value.EnumerateObject())
                            child.Parameters[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                }

                found.Add(child);
            }
        }
        catch (JsonException)
        {
            // Not readable, so the branch never runs and there is nothing to ask for.
        }

        return found;
    }
}
