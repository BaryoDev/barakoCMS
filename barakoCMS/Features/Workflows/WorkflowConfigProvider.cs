using System.Security.Claims;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Config;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>A workflow as its document: what triggers it and what it does, with no credential.</summary>
internal sealed record WorkflowConfig(
    List<string>? TriggerContentTypes,
    List<string>? TriggerEvents,
    Dictionary<string, string>? Conditions,
    List<WorkflowAction>? Actions,
    bool Enabled = true,
    ScheduleTrigger? Schedule = null,
    string? NotifyRoleOnFailure = null);

/// <summary>
/// The <c>workflows</c> section: each workflow of the tenant under its name.
/// </summary>
/// <remarks>
/// <para>
/// A credential parameter (a webhook <c>Secret</c>, anything <c>CredentialNames</c> reads as
/// one) is never exported, and a document that carries one is refused: a configuration file is
/// reviewed and committed, which is exactly where a credential must not be. Applying a change keeps
/// the credentials a stored action holds only when that action keeps its position, its type and
/// every other parameter. Any other change to an action holding one drops it, destructively, so a
/// secret is never carried to an address its owner did not enter it for.
/// </para>
/// <para>
/// A workflow the document leaves out is deleted, destructively, unless a module seeded it
/// (<see cref="WorkflowDefinition.OwnerModule"/>). A module-owned workflow is never exported and
/// never planned for delete, and a document naming one with other content is refused. Workflows are keyed by name, so
/// two stored under one name cannot be told apart: the export carries the first and names the rest
/// as a gap, and a document naming one is refused until they are renamed.
/// </para>
/// </remarks>
internal sealed class WorkflowConfigProvider(
    IDocumentSession session,
    IWorkflowSchemaValidator validator,
    TenantContext tenant,
    IPermissionResolver permissions,
    IWorkflowPluginRegistry registry) : IConfigProvider
{
    public string Section => "workflows";

    public int Order => 400;

    public string ReadCapability => SystemCapabilities.ManageWorkflows;

    public string WriteCapability => SystemCapabilities.ManageWorkflows;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var gaps = new List<string>();
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // A module-owned workflow is the module's code, marked as the module's when it seeds, so a document
        // does not carry it.
        foreach (var group in (await session.Query<WorkflowDefinition>().ToListAsync(ct))
                     .Where(w => w.OwnerModule is null).GroupBy(w => w.Name, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(w => w.Id).ToList();
            if (ordered.Count > 1)
            {
                gaps.Add($"{ordered.Count} workflows are named '{group.Key}'. A workflow is keyed by its name, so only one is exported.");
            }

            var (config, held) = Of(ordered[0]);
            documents[group.Key] = ConfigJson.ToElement(config);
            gaps.AddRange(held.Select(h => $"'{group.Key}': {h}"));
        }

        return new ConfigSectionExport { Documents = documents, Gaps = gaps };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();
        var stored = (await session.Query<WorkflowDefinition>().ToListAsync(ct)).GroupBy(w => w.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (name, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            stored.TryGetValue(name, out var held);
            if (held is { Count: > 1 })
            {
                errors.Add($"'{name}': {held.Count} workflows are stored under this name. Rename or delete all but one first.");
                continue;
            }

            if (await ResolveAsync(name, document, errors, ct) is not { } workflow)
            {
                continue;
            }

            // A document applied by a person is held to that person's rights over the content the
            // workflow creates and moves. The host's own file (no caller) is the operator's to trust.
            if (caller.Principal is { } who)
            {
                var missing = await WorkflowSaveRights.MissingAsync(session, permissions, registry, who, workflow, ct);
                if (missing.Count > 0)
                {
                    errors.AddRange(missing.Select(m => $"'{name}': {m}"));
                    continue;
                }
            }

            var change = Plan(name, workflow, held?.Single(), document);
            if (held?.Single() is { OwnerModule: { } owner })
            {
                if (change is not null)
                {
                    errors.Add($"'{name}': this workflow belongs to the {owner} module and is set by the module, so a document cannot change it. "
                               + "Leave it out of the document, or name it exactly as it is stored.");
                }

                continue;
            }

            if (change is not null)
            {
                changes.Add(change);
            }
        }

        changes.AddRange(stored.Where(entry => !desired.ContainsKey(entry.Key) && entry.Value.All(w => w.OwnerModule is null))
            .Select(entry => entry.Key).Order(StringComparer.Ordinal).Select(name =>
            new ConfigChange { Key = name, Kind = ConfigChangeKind.Destructive, Summary = $"delete workflow '{name}'" }));

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var actor = caller.Principal ?? new ClaimsPrincipal();

        foreach (var change in plan.Changes)
        {
            var current = await session.Query<WorkflowDefinition>().FirstOrDefaultAsync(w => w.Name == change.Key, ct);

            if (change.Desired is not { } document)
            {
                if (current is not null && await DeleteAsync(current, actor, ct) is { } refused)
                {
                    errors.Add(refused);
                }

                continue;
            }

            if (await ResolveAsync(change.Key, document, errors, ct) is not { } workflow)
            {
                continue;
            }

            workflow.Id = current?.Id ?? Guid.NewGuid();
            if (current is not null)
            {
                KeepCredentials(workflow, current);
            }

            // Not protected here: the document carries no credential, and what was carried over from
            // the stored workflow is ciphertext already, which protecting again would make unreadable.
            session.Store(workflow);
            await WorkflowAudit.RecordAsync(session, tenant.Slug, current is null ? "workflow.created" : "workflow.updated", workflow, actor, ct: ct);
        }

        return errors;
    }

    /// <summary>A workflow as its document, and the credentials it holds that the document leaves out.</summary>
    private static (WorkflowConfig Config, List<string> Held) Of(WorkflowDefinition workflow)
    {
        var held = new List<string>();
        var actions = new List<WorkflowAction>();

        for (var i = 0; i < workflow.Actions.Count; i++)
        {
            var action = workflow.Actions[i];
            var parameters = Shown(action, out var unreadable);
            if (WebhookSigning.HasCredential(action.Type, action.Parameters))
            {
                held.Add($"action {i} ({action.Type}) holds a credential, which is not exported. Set it again where the document is applied.");
            }

            held.AddRange(unreadable.Select(b => $"action {i} ({action.Type}) has a branch '{b}' that cannot be read, so it is left out."));
            actions.Add(new WorkflowAction { Type = action.Type, Parameters = parameters, OnFailure = action.OnFailure });
        }

        // A schedule workflow has no events of its own: its stored trigger is derived from its name.
        var config = new WorkflowConfig(
            WorkflowTriggers.ContentTypes(workflow), workflow.Schedule is null ? WorkflowTriggers.Events(workflow) : [],
            new Dictionary<string, string>(workflow.Conditions ?? [], StringComparer.Ordinal), actions, workflow.Enabled,
            workflow.Schedule is { } schedule ? Copy(schedule) : null,
            string.IsNullOrWhiteSpace(workflow.NotifyRoleOnFailure) ? null : workflow.NotifyRoleOnFailure);
        return (config, held);
    }

    /// <summary>The document as the definition it would store, checked the way the create endpoint checks one.</summary>
    private async Task<WorkflowDefinition?> ResolveAsync(string name, JsonElement document, List<string> errors, CancellationToken ct)
    {
        if (ConfigJson.Read<WorkflowConfig>(name, document, errors) is not { } config)
        {
            return null;
        }

        var workflow = new WorkflowDefinition
        {
            Name = name,
            TriggerContentTypes = config.TriggerContentTypes ?? [],
            TriggerEvents = config.TriggerEvents ?? [],
            Conditions = config.Conditions ?? [],
            Actions = (config.Actions ?? []).Where(a => a is not null).ToList(),
            Enabled = config.Enabled,
            Schedule = config.Schedule is { } schedule ? Copy(schedule) : null,
            NotifyRoleOnFailure = string.IsNullOrWhiteSpace(config.NotifyRoleOnFailure) ? null : config.NotifyRoleOnFailure,
        };
        foreach (var action in workflow.Actions)
        {
            action.Parameters ??= [];
        }

        if (workflow.Actions.Any(a => WebhookSigning.HasCredential(a.Type, a.Parameters)))
        {
            errors.Add($"'{name}': an action carries a credential, or a branch that cannot be read and so cannot be checked for one. "
                       + "A configuration document holds none; set it on the workflow after it is applied.");
            return null;
        }

        // Holds no credential now, so this only takes off the flags a read response writes onto a
        // branch's children, which are not the workflow's to store.
        foreach (var action in workflow.Actions)
        {
            action.Parameters = Shown(action, out _);
        }

        if (!await PrepareScheduleAsync(name, workflow, errors, ct))
        {
            return null;
        }

        WorkflowTriggers.Normalise(workflow);
        var validation = await validator.ValidateAsync(workflow, ct);
        if (!validation.IsValid)
        {
            errors.AddRange(validation.Errors.Select(e => $"'{name}': {e.Field}: {e.Message}"));
            return null;
        }

        if (workflow.Schedule is not null)
        {
            return Scheduled(name, workflow);
        }

        if (validation.NormalisedTriggerEvents is { Count: > 0 } declared)
        {
            workflow.TriggerEvents = declared;
        }
        else if (validation.NormalisedTriggerEvent is { Length: > 0 } single)
        {
            workflow.TriggerEvents = [single];
        }

        workflow.TriggerEvent = string.Empty;
        WorkflowTriggers.Normalise(workflow);
        return workflow;
    }

    /// <summary>
    /// Checks a schedule, and for a schedule workflow stands an ordinary event in for its trigger so
    /// the validator can check the rest. True when there is no schedule or it is fine.
    /// </summary>
    /// <remarks>
    /// Named by the schedule and not by events: the stored event is not one the validator knows.
    /// </remarks>
    private async Task<bool> PrepareScheduleAsync(string name, WorkflowDefinition workflow, List<string> errors, CancellationToken ct)
    {
        if (workflow.Schedule is not { } due)
        {
            return true;
        }

        if (await ScheduleErrorAsync(name, due, ct) is { } error)
        {
            errors.Add(error);
            return false;
        }

        workflow.TriggerContentTypes = [due.ContentType];
        workflow.TriggerEvents = [WorkflowEvents.Created];
        return true;
    }

    /// <summary>The schedule workflow as it is stored: its content type, and an event only its own name carries.</summary>
    private static WorkflowDefinition Scheduled(string name, WorkflowDefinition workflow)
    {
        workflow.TriggerEvents = [ScheduleOffset.EventOf(name)];
        workflow.TriggerEvent = string.Empty;
        WorkflowTriggers.Normalise(workflow);
        return workflow;
    }

    private static ScheduleTrigger Copy(ScheduleTrigger schedule) => new()
    {
        ContentType = schedule.ContentType,
        DateField = schedule.DateField,
        Offset = schedule.Offset,
        InState = schedule.InState,
        Where = new Dictionary<string, string>(schedule.Where ?? [], StringComparer.Ordinal),
    };

    /// <summary>What is wrong with a schedule, in the words of the other trigger errors, or null.</summary>
    private async Task<string?> ScheduleErrorAsync(string name, ScheduleTrigger schedule, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schedule.DateField))
        {
            return $"'{name}': schedule.dateField: A date field is required";
        }

        if (!ScheduleOffset.TryParse(schedule.Offset, out _))
        {
            return $"'{name}': schedule.offset: Use a whole number plus m, h or d, such as -24h, of at most 365 days";
        }

        if (string.IsNullOrWhiteSpace(schedule.ContentType)
            || !await session.Query<ContentTypeDefinition>().AnyAsync(barakoCMS.Core.ContentTypeName.Named(schedule.ContentType), ct))
        {
            return $"'{name}': schedule.contentType: Content type '{schedule.ContentType}' does not exist";
        }

        return null;
    }

    private static ConfigChange? Plan(string name, WorkflowDefinition workflow, WorkflowDefinition? current, JsonElement document)
    {
        var desired = Of(workflow).Config;
        if (current is null)
        {
            return new ConfigChange
            {
                Key = name, Kind = ConfigChangeKind.Create, Desired = document,
                Summary = $"create workflow '{name}' with {workflow.Actions.Count} actions",
            };
        }

        var (held, _) = Of(current);
        if (ConfigJson.Same(held, desired))
        {
            return null;
        }

        var lost = LostCredentials(workflow, current);
        var details = new List<string>();
        AddIfChanged(details, "triggers", (held.TriggerContentTypes, held.TriggerEvents), (desired.TriggerContentTypes, desired.TriggerEvents));
        AddIfChanged(details, "conditions", held.Conditions, desired.Conditions);
        AddIfChanged(details, "schedule", held.Schedule, desired.Schedule);
        AddIfChanged(details, "actions", held.Actions, desired.Actions);
        AddIfChanged(details, "the role notified on failure", held.NotifyRoleOnFailure, desired.NotifyRoleOnFailure);
        AddIfChanged(details, held.Enabled ? "is switched off" : "is switched on", held.Enabled, desired.Enabled);
        details.AddRange(lost.Select(i => $"action {i} changes, so the credential its stored copy held is dropped and has to be set again"));

        return new ConfigChange
        {
            Key = name, Kind = lost.Count > 0 ? ConfigChangeKind.Destructive : ConfigChangeKind.Change,
            Desired = document, Summary = $"change workflow '{name}'", Details = details,
        };
    }

    private static void AddIfChanged<T>(List<string> details, string what, T before, T after)
    {
        if (!ConfigJson.Same(before, after))
        {
            details.Add(what.StartsWith("is ", StringComparison.Ordinal) ? what : $"{what} change");
        }
    }

    /// <summary>
    /// Gives each action the stored action's parameters, credentials included, when the stored
    /// action at the same position has the same type and every other parameter the same. Any
    /// difference, a reorder included, drops what the stored action held.
    /// </summary>
    /// <remarks>
    /// The rule a connector follows when its base URL moves: a credential entered for one address
    /// must not be sent to another by somebody who never saw it. Matching on type alone let a
    /// changed <c>Url</c> carry a webhook's signing secret to wherever the document pointed it.
    /// </remarks>
    private static void KeepCredentials(WorkflowDefinition workflow, WorkflowDefinition current)
    {
        for (var i = 0; i < workflow.Actions.Count && i < current.Actions.Count; i++)
        {
            if (Unchanged(workflow.Actions[i], current.Actions[i]))
            {
                workflow.Actions[i].Parameters = new Dictionary<string, string>(current.Actions[i].Parameters);
            }
        }
    }

    /// <summary>The positions of the stored actions that hold a credential the document does not keep.</summary>
    private static List<int> LostCredentials(WorkflowDefinition workflow, WorkflowDefinition current)
    {
        var lost = new List<int>();
        for (var i = 0; i < current.Actions.Count; i++)
        {
            var stored = current.Actions[i];
            if (HoldsCredential(stored) && (i >= workflow.Actions.Count || !Unchanged(workflow.Actions[i], stored)))
            {
                lost.Add(i);
            }
        }

        return lost;
    }

    private static bool HoldsCredential(WorkflowAction action) =>
        WebhookSigning.HasCredential(action.Type, action.Parameters);

    /// <summary>
    /// Same type, and every parameter but the credentials the same, a Conditional's branch children
    /// included, with the read response's flags off both sides.
    /// </summary>
    private static bool Unchanged(WorkflowAction desired, WorkflowAction stored) =>
        string.Equals(desired.Type, stored.Type, StringComparison.Ordinal)
        && ConfigJson.Same(Shown(stored, out _), Shown(desired, out _));

    /// <summary>An action's parameters as a document carries them: no credential and no response flag.</summary>
    private static Dictionary<string, string> Shown(WorkflowAction action, out List<string> unreadable) =>
        WebhookSigning.WithoutSecretOrFlags(action.Type, action.Parameters ?? [], out unreadable);

    /// <summary>Deletes a workflow the way <c>DELETE /api/workflows/{id}</c> does, or says why it will not.</summary>
    private async Task<string?> DeleteAsync(WorkflowDefinition workflow, ClaimsPrincipal actor, CancellationToken ct)
    {
        await WorkflowDefinitionLock.TakeAsync(session, workflow.Id, ct);

        var workflowId = workflow.Id;
        var queued = await session.Query<WorkflowRun>()
            .CountAsync(r => r.WorkflowDefinitionId == workflowId && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running), ct);
        if (queued > 0)
        {
            return $"workflow '{workflow.Name}' has {queued} runs queued or running. Switch it off and let them finish, "
                   + "or delete it with DELETE /api/workflows/{id}, which cancels them.";
        }

        session.Delete(workflow);
        await WorkflowAudit.RecordAsync(session, tenant.Slug, "workflow.deleted", workflow, actor, ct: ct);
        return null;
    }
}
