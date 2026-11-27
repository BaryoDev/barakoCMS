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
/// CreateContent, CreateEntry, CreateTask, UpdateField and Transition run as the system, with the permission checks skipped. Saving a
/// workflow is the one moment a person chooses what they do, so that person is asked then: they
/// need create on each type a CreateContent, CreateEntry or CreateTask action names, update on each type an
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
        ClaimsPrincipal principal, WorkflowDefinition workflow, CancellationToken ct,
        barakoCMS.Core.Interfaces.ISensitivityService? sensitivity = null)
    {
        var needs = Needs(workflow).ToList();
        var capabilities = CapabilityNeeds(registry, workflow).ToList();
        var queries = Flatten(workflow)
            .Where(a => string.Equals(a.Type, "Query", StringComparison.Ordinal))
            .Select(a => a.Parameters.GetValueOrDefault("Query")?.Trim() ?? "")
            .Where(slug => slug.Length > 0 && !slug.Contains("{{"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var templates = Flatten(workflow)
            .Where(a => string.Equals(a.Type, "Email", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Parameters.GetValueOrDefault(barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.TemplateParameter)?.Trim() ?? "")
            .Where(name => name.Length > 0 && !name.Contains("{{"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var requests = RequestSlugs(workflow);
        var missing = new List<string>();
        if (needs.Count == 0 && capabilities.Count == 0 && queries.Count == 0 && templates.Count == 0 && requests.Count == 0 && !HasTemplatedRequest(workflow))
            return missing;

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

        // A Query step reads as the user behind the event, not as whoever saved it. This check holds
        // only at save: it judges the saver, so an author cannot name a field they may not read. It
        // does not bind later runs, which check again as the event's actor, and a query saved or
        // changed after the workflow is checked only then.
        if (queries.Count > 0)
        {
            var runner = new barakoCMS.Infrastructure.Connectors.QueryRunner(session, permissions, sensitivity);
            foreach (var slug in queries)
            {
                var saved = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
                if (saved is null) continue;

                string? refusal = user is null
                    ? "Who is saving this could not be found."
                    : await ((barakoCMS.Infrastructure.Connectors.IContextQueryRunner)runner).ValidateAsync(
                        saved, new barakoCMS.Infrastructure.Connectors.QueryRunContext(null) { AsActor = true, Actor = user.Id }, ct);

                if (refusal is not null)
                    missing.Add($"You cannot read everything the saved query '{slug}' reads, so you cannot save a workflow that runs it. {refusal}");
            }
        }

        missing.AddRange(await TemplateDatasetsMissingAsync(session, permissions, sensitivity, user, templates, ct));
        missing.AddRange(await RequestGrantsMissingAsync(session, permissions, sensitivity, principal, user, workflow, requests, ct));
        missing.AddRange(await TemplatedRequestMissingAsync(session, sensitivity, principal, workflow, ct));
        return missing;
    }

    /// <summary>Whether a Request step names its request by a template, which the engine resolves from entry data at run time.</summary>
    private static bool HasTemplatedRequest(WorkflowDefinition workflow) =>
        Flatten(workflow).Any(a => string.Equals(a.Type, "Request", StringComparison.OrdinalIgnoreCase)
            && (a.Parameters.GetValueOrDefault("Request") ?? "").Contains("{{"));

    /// <summary>
    /// A templated slug can name any request of the tenant, so the saver must be able to read every
    /// granted field of every connector that has grants. A tenant with no granted connector asks
    /// for nothing, so a workflow written before grants existed still saves.
    /// </summary>
    private static async Task<List<string>> TemplatedRequestMissingAsync(
        IDocumentSession session, barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
        ClaimsPrincipal principal, WorkflowDefinition workflow, CancellationToken ct)
    {
        var missing = new List<string>();
        if (!HasTemplatedRequest(workflow)) return missing;

        var connectors = (await session.Query<Connector>().ToListAsync(ct)).Where(c => c.AllowedFields is { Count: > 0 });
        foreach (var connector in connectors.OrderBy(c => c.Slug, StringComparer.Ordinal))
        {
            foreach (var entry in connector.AllowedFields!)
            {
                if (!barakoCMS.Infrastructure.Connectors.ConnectorGrants.TryParse(entry, out var type, out var name)) continue;

                var schema = await AccountContentTypes.ResolveAsync(session, type, ct);
                var field = schema?.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (schema is null || field is null) continue;

                if (sensitivity is null || !await sensitivity.MayReadFieldAsync(schema.Name, field, null, principal, ct))
                {
                    missing.Add($"You cannot read '{schema.Name}.{field.Name}', which the connector '{connector.Slug}' is granted, and this workflow "
                                + "names its request by a template, so it could send it. Name the request, or ask for the read.");
                }
            }
        }

        return missing;
    }

    /// <summary>The requests a workflow's Request steps name, by plain slug.</summary>
    private static List<string> RequestSlugs(WorkflowDefinition workflow) =>
        Flatten(workflow)
            .Where(a => string.Equals(a.Type, "Request", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Parameters.GetValueOrDefault("Request")?.Trim() ?? "")
            .Where(slug => slug.Length > 0 && !slug.Contains("{{"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A Request step sends what its connector was granted, as the system, so the saver is asked
    /// then, as for a Query step: they must be able to read each granted field a template of the
    /// request names, on each type the workflow triggers on. Without a way to ask, they cannot.
    /// </summary>
    private static async Task<List<string>> RequestGrantsMissingAsync(
        IDocumentSession session, IPermissionResolver permissions, barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
        ClaimsPrincipal principal, User? user, WorkflowDefinition workflow, List<string> requests, CancellationToken ct)
    {
        var missing = new List<string>();
        var types = workflow.TriggerContentTypes.Append(workflow.TriggerContentType)
            .Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var slug in requests)
        {
            var request = await session.Query<RequestDefinition>().FirstOrDefaultAsync(r => r.Slug == slug, ct);
            var connector = request is null
                ? null
                : await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == request.ConnectorSlug, ct);
            if (request is null || connector?.AllowedFields is not { Count: > 0 }) continue;

            var grants = barakoCMS.Infrastructure.Connectors.ConnectorGrants.SetOf(connector.AllowedFields);
            var templates = request.HeaderTemplates.Values.Append(request.PathTemplate).Append(request.BodyTemplate).ToList();
            missing.AddRange(await QueryRowsMissingAsync(session, permissions, sensitivity, user, request, connector, templates, ct));
            foreach (var type in types)
            {
                var schema = await AccountContentTypes.ResolveAsync(session, type, ct);
                if (schema is null) continue;

                foreach (var name in templates.SelectMany(t => barakoCMS.Infrastructure.Connectors.ConnectorGrants.GrantedReads(t, schema, grants)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var field = schema.Fields.First(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (sensitivity is null || !await sensitivity.MayReadFieldAsync(schema.Name, field, null, principal, ct))
                    {
                        missing.Add($"You cannot read '{schema.Name}.{field.Name}', which the connector '{connector.Slug}' is granted and the request '{slug}' sends, so you cannot save a workflow that runs it.");
                    }
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// A request whose templates hold <c>{{query.*}}</c> holes sends the rows of its query, and with
    /// grants those rows carry granted fields. The saver must be able to read all the query reads,
    /// as for a Query step, including a followed reference field on the type it points at. A query
    /// that cannot be found or judged refuses, since nothing says what it would send.
    /// </summary>
    private static async Task<List<string>> QueryRowsMissingAsync(
        IDocumentSession session, IPermissionResolver permissions, barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
        User? user, RequestDefinition request, Connector connector, List<string?> templates, CancellationToken ct)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(request.QuerySlug)
            || !templates.Any(t => t is not null && t.Contains("query.", StringComparison.OrdinalIgnoreCase)))
        {
            return missing;
        }

        var slug = request.QuerySlug.Trim();
        var saved = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
        string? refusal;
        if (saved is null)
        {
            refusal = "That query could not be found.";
        }
        else if (user is null)
        {
            refusal = "Who is saving this could not be found.";
        }
        else
        {
            var runner = new barakoCMS.Infrastructure.Connectors.QueryRunner(session, permissions, sensitivity);
            refusal = await ((barakoCMS.Infrastructure.Connectors.IContextQueryRunner)runner).ValidateAsync(
                saved, new barakoCMS.Infrastructure.Connectors.QueryRunContext(null) { AsActor = true, Actor = user.Id }, ct);
        }

        if (refusal is not null)
        {
            missing.Add($"You cannot read everything the saved query '{slug}' reads, which the request '{request.Slug}' sends "
                        + $"through the connector '{connector.Slug}', so you cannot save a workflow that runs it. {refusal}");
        }

        return missing;
    }

    /// <summary>
    /// The datasets of every template an Email step names are read as the template's fields are:
    /// the saver must be able to read what each one reads, as for a Query step.
    /// </summary>
    private static async Task<List<string>> TemplateDatasetsMissingAsync(
        IDocumentSession session, IPermissionResolver permissions, barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
        User? user, List<string> templates, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var name in templates)
        {
            var template = await barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.FindAsync(session, name, ct);
            if (template is null) continue;

            var (datasets, problem) = barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.DatasetsOf(template.Data);
            if (problem is not null) missing.Add($"The email template '{name}' cannot be sent by a workflow you save. {problem}");
            foreach (var refusal in await TemplateDatasetRights.RefusalsAsync(session, permissions, sensitivity, user, datasets, ct))
                missing.Add($"The email template '{name}' cannot be sent by a workflow you save. {refusal}");
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
            else if (string.Equals(action.Type, "CreateEntry", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Every action a workflow runs: those nested in a Conditional, and each compensation, included.</summary>
    internal static IEnumerable<WorkflowAction> Flatten(WorkflowDefinition workflow)
    {
        foreach (var action in workflow.Actions)
        {
            foreach (var each in Expand(action)) yield return each;
            if (action.Compensate is { } undo)
                yield return new WorkflowAction { Type = undo.Type, Parameters = undo.Parameters };
        }
    }

    internal static IEnumerable<WorkflowAction> Expand(WorkflowAction action)
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
