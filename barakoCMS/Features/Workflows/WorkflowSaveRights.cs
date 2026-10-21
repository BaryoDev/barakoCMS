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
/// CreateContent and Transition run as the system, with the permission checks skipped. Saving a
/// workflow is the one moment a person chooses what they do, so that person is asked then: they
/// need create on each type a CreateContent action names, and the transition right on each type
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
        IDocumentSession session, IPermissionResolver permissions,
        ClaimsPrincipal principal, WorkflowDefinition workflow, CancellationToken ct)
    {
        var needs = Needs(workflow).ToList();
        var missing = new List<string>();
        if (needs.Count == 0) return missing;

        User? user = null;
        if (Guid.TryParse(principal.FindFirst("UserId")?.Value, out var id))
            user = await session.LoadAsync<User>(id, ct);

        foreach (var (type, action, label) in needs.Distinct())
        {
            if (user is null || type.Contains("{{") || action.Contains("{{")
                || !await permissions.CanPerformActionAsync(user, type, action, null, ct))
            {
                missing.Add(label);
            }
        }

        return missing;
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
            else if (string.Equals(action.Type, "Transition", StringComparison.OrdinalIgnoreCase))
            {
                var name = action.Parameters.GetValueOrDefault("Transition")?.Trim() ?? "";
                yield return (type, PermissionResolver.TransitionActionPrefix + name,
                    $"You cannot make the '{name}' transition on '{type}' entries, so you cannot save a workflow that does.");
            }
        }
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
