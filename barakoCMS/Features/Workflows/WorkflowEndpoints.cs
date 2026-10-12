using FastEndpoints;
using FluentValidation;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

internal class CreateWorkflowEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IWorkflowSchemaValidator validator,
    ISecretProtector protector) : Endpoint<CreateWorkflowRequest, barakoCMS.Features.Workflows.WorkflowResponse>
{
    public override void Configure()
    {
        Post("/api/workflows");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateWorkflowRequest req, CancellationToken ct)
    {
        var workflow = req.ToDefinition();

        // Validate before persisting so invalid trigger events / unknown action types / missing
        // required parameters are rejected up front rather than silently never firing (or firing twice).
        var validation = await validator.ValidateAsync(workflow, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                AddError($"{error.Field}: {error.Message}");
            }
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        WorkflowSave.Normalise(workflow, validation);

        // Encrypted before it is stored, so the definition, the runs that copy its parameters and
        // the execution log all hold ciphertext. Only the webhook action decrypts it, when sending.
        WebhookSigning.ProtectSecrets(workflow, protector);

        workflow.Id = Guid.NewGuid();
        session.Store(workflow);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(
            barakoCMS.Features.Workflows.WorkflowResponse.Saved(workflow, validation.Warnings), cancellation: ct);
    }
}

internal static class WorkflowSave
{
    /// <summary>Stores the triggers in the form the validator matched them in.</summary>
    /// <remarks>
    /// Stored as the content type declares it. The engine matches this with an equality query, so
    /// a workflow saved as "transition:approve" against a transition named "Approve" would be
    /// accepted and then never fire.
    /// </remarks>
    internal static void Normalise(
        WorkflowDefinition workflow, WorkflowValidationResult validation)
    {
        if (validation.NormalisedTriggerEvents is { Count: > 0 } declared)
        {
            workflow.TriggerEvent = declared[0];
            workflow.TriggerEvents = declared;
        }
        else if (validation.NormalisedTriggerEvent is { Length: > 0 } single)
        {
            workflow.TriggerEvent = single;
        }

        WorkflowTriggers.Normalise(workflow);

        // Stored as none rather than null, so no reader has to tell the two apart.
        workflow.Conditions ??= [];
    }
}

internal class ListWorkflowsEndpoint(
    IDocumentSession session) : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.Workflows.WorkflowResponse>>
{
    public override void Configure()
    {
        Get("/api/workflows");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<WorkflowDefinition>()
            .OrderBy(w => w.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<barakoCMS.Features.Workflows.WorkflowResponse>
        {
            Items = page.Items.Select(barakoCMS.Features.Workflows.WorkflowResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal static class WorkflowAudit
{
    internal static Task RecordAsync(
        IDocumentSession session,
        string tenantSlug,
        string action,
        WorkflowDefinition workflow,
        System.Security.Claims.ClaimsPrincipal user,
        Dictionary<string, object>? extra = null,
        CancellationToken ct = default)
    {
        var actorId = Guid.TryParse(user.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;

        // The name and nothing of the actions. Their parameters hold addresses and credentials, and
        // the audit log is the one table nothing is ever deleted from.
        var metadata = new Dictionary<string, object> { ["name"] = workflow.Name };

        if (extra is not null)
        {
            foreach (var (key, value) in extra) metadata[key] = value;
        }

        return AuditLog.RecordAsync(session, tenantSlug, action, actorId,
            user.FindFirst("Username")?.Value,
            targetType: nameof(WorkflowDefinition), targetId: workflow.Id.ToString(),
            metadata: metadata, ct: ct);
    }
}

internal sealed class SetWorkflowEnabledRequest
{
    public Guid Id { get; set; }

    /// <summary>Nullable so a body that leaves it out is refused and not read as off.</summary>
    public bool? Enabled { get; set; }
}

internal sealed class SetWorkflowEnabledValidator : Validator<SetWorkflowEnabledRequest>
{
    public SetWorkflowEnabledValidator()
    {
        RuleFor(x => x.Enabled).NotNull().WithMessage("enabled must be true or false.");
    }
}

/// <summary>Switches one workflow on or off.</summary>
/// <remarks>
/// Its own endpoint because workflows have no update, and switching one off is the thing an
/// operator needs in a hurry: a workflow pointed at the wrong place fires on every publish until
/// somebody can stop it.
///
/// Off, the workflow starts no runs. Runs it had already queued are not touched here: the runner
/// cancels each one when it reaches it, so this request does not grow with the queue, and a run
/// queued a moment before the switch is caught the same way.
/// </remarks>
internal sealed class SetWorkflowEnabledEndpoint(
    IDocumentSession session,
    TenantContext tenant) : Endpoint<SetWorkflowEnabledRequest, WorkflowResponse>
{
    public override void Configure()
    {
        Put("/api/workflows/{id}/enabled");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(SetWorkflowEnabledRequest req, CancellationToken ct)
    {
        var workflow = await session.LoadAsync<WorkflowDefinition>(req.Id, ct);
        if (workflow is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var enabled = req.Enabled ?? workflow.Enabled;

        // Nothing is written or recorded when nothing changed, so the audit trail holds switches
        // and not repeated requests.
        if (workflow.Enabled != enabled)
        {
            workflow.Enabled = enabled;

            // An update and not a store. A store is an upsert, and a delete committed after the
            // load above would be undone by it: the workflow would be inserted again.
            session.Update(workflow);

            await WorkflowAudit.RecordAsync(session, tenant.Slug,
                enabled ? "workflow.enabled" : "workflow.disabled", workflow, User, ct: ct);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex.GetType().Name.Contains("NonExistentDocument"))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        await Send.ResponseAsync(WorkflowResponse.From(workflow), cancellation: ct);
    }
}

internal sealed class DeleteWorkflowRequest
{
    public Guid Id { get; set; }
}

/// <summary>Deletes one workflow, cancelling the runs it still has queued.</summary>
/// <remarks>
/// Cancels and does not refuse. A workflow somebody wants gone is usually one that keeps failing,
/// and a failing workflow always has an attempt waiting on its backoff, so refusing while a run is
/// queued would refuse exactly the delete this exists for. Leaving the runs would not stop it
/// either: a run carries a copy of its actions and goes on without the definition.
///
/// The cancels, the delete and the audit entry are one transaction. A run the runner writes in
/// between refuses the whole of it, and the request is sent again.
/// </remarks>
internal sealed class DeleteWorkflowEndpoint(
    IDocumentSession session,
    TenantContext tenant) : Endpoint<DeleteWorkflowRequest>
{
    /// <summary>How many queued runs one delete cancels. Past it the delete is refused.</summary>
    /// <remarks>
    /// Each run is loaded and written under its own version check, so the number bounds the
    /// transaction. A workflow with more waiting than this is switched off first, which lets the
    /// runner cancel them as it goes.
    /// </remarks>
    internal const int MaxRunsCancelled = 200;

    public override void Configure()
    {
        Delete("/api/workflows/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(DeleteWorkflowRequest req, CancellationToken ct)
    {
        // Before anything is read. A retry of one of this workflow's runs takes the same lock, so it
        // either queues its attempt before the runs are looked for below, and is cancelled with
        // them, or waits and finds the workflow gone.
        await WorkflowDefinitionLock.TakeAsync(session, req.Id, ct);

        var workflow = await session.LoadAsync<WorkflowDefinition>(req.Id, ct);
        if (workflow is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var workflowId = workflow.Id;

        var queued = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId
                && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running))
            .OrderBy(r => r.CreatedAt)
            .Take(MaxRunsCancelled + 1)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (queued.Count > MaxRunsCancelled)
        {
            ThrowError(
                $"That workflow has more than {MaxRunsCancelled} runs queued, which is more than one delete cancels. "
              + "Switch it off first: the runner cancels its queued runs as it reaches them, and it can be deleted after that.",
                409);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cancelled = 0;

        foreach (var runId in queued)
        {
            // Loaded one at a time, the way the runner claims, so each write carries the version it
            // read and a run the runner took in between is refused and not overwritten.
            var run = await session.LoadAsync<WorkflowRun>(runId, ct);

            // Looked at again, because the run may have finished since the query above listed it.
            // A run that succeeded or failed in between is history and is not marked cancelled.
            if (run is null || run.Status is not (RunStatus.Pending or RunStatus.Running)) continue;

            run.Cancel(now);
            session.Update(run);
            cancelled++;
        }

        session.Delete(workflow);

        await WorkflowAudit.RecordAsync(session, tenant.Slug, "workflow.deleted", workflow, User,
            new Dictionary<string, object> { ["cancelledRuns"] = cancelled }, ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            ThrowError("A run of that workflow moved while it was being deleted. Nothing was changed. Send the request again.", 409);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

internal sealed class UpdateWorkflowRequest : CreateWorkflowRequest
{
    public Guid Id { get; set; }
}

/// <summary>Replaces one workflow with the definition sent, validated the way a create is.</summary>
/// <remarks>
/// <para>
/// A full update: every field the create takes is replaced, and a field left out takes the create's
/// default, so leaving out <c>enabled</c> switches the workflow on as it would a new one. The id is
/// the route's.
/// </para>
/// <para>
/// Credentials are write-only, so a caller holding a workflow read back from the API never has
/// them to send again. An action sent back unchanged, in the same position with the same type and
/// every shown parameter as the API returned it, keeps its stored credentials, its Conditional
/// children's included. A changed action that leaves a credential out is refused with a 400 naming
/// it: a credential stays with the destination it was given for. Sending one blank clears it, and
/// sending a value replaces it. A changed Conditional branch is taken as sent.
/// </para>
/// <para>
/// Runs already queued keep the copy of the actions they were queued with. The change applies to
/// runs queued from here on.
/// </para>
/// </remarks>
internal sealed class UpdateWorkflowEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IWorkflowSchemaValidator validator,
    ISecretProtector protector,
    TenantContext tenant) : Endpoint<UpdateWorkflowRequest, WorkflowResponse>
{
    public override void Configure()
    {
        Put("/api/workflows/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(UpdateWorkflowRequest req, CancellationToken ct)
    {
        var stored = await session.LoadAsync<WorkflowDefinition>(req.Id, ct);
        if (stored is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var workflow = req.ToDefinition();

        var validation = await validator.ValidateAsync(workflow, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                AddError($"{error.Field}: {error.Message}");
            }
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        WorkflowSave.Normalise(workflow, validation);
        var (kept, refusal) = StoredValuesToKeep(workflow, stored);
        if (refusal is not null)
        {
            AddError(refusal);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        WebhookSigning.ProtectSecrets(workflow, protector);
        foreach (var (index, name, value) in kept)
        {
            workflow.Actions[index].Parameters[name] = value;
        }

        workflow.Id = stored.Id;

        // An update and not a store, so a delete committed after the load above is not undone by
        // this inserting the workflow again.
        session.Update(workflow);
        await WorkflowAudit.RecordAsync(session, tenant.Slug, "workflow.updated", workflow, User, ct: ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("NonExistentDocument"))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(WorkflowResponse.Saved(workflow, validation.Warnings), cancellation: ct);
    }

    /// <summary>
    /// The stored, already protected values to keep, by action position, or the reason a credential
    /// cannot be kept.
    /// </summary>
    /// <remarks>
    /// Read before the request's own credentials are protected, so a value sent blank still counts
    /// as sent. An action is unchanged when its type and every parameter the API shows are what a
    /// read of the stored action returns, a Conditional's branches included. Only then is a
    /// credential kept, because a credential follows the action it was given for: kept on a webhook
    /// whose URL changed, or on a second webhook moved into the first one's place, it would sign
    /// requests to a destination nobody gave it to.
    /// </remarks>
    private static (List<(int Index, string Name, string Value)> Kept, string? Refusal) StoredValuesToKeep(
        WorkflowDefinition incoming, WorkflowDefinition stored)
    {
        var kept = new List<(int, string, string)>();
        for (var i = 0; i < incoming.Actions.Count && i < stored.Actions.Count; i++)
        {
            var next = incoming.Actions[i];
            var before = stored.Actions[i];
            if (next is null || before is null
                || !string.Equals(next.Type, before.Type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            next.Parameters ??= new Dictionary<string, string>();
            var storedParameters = before.Parameters ?? [];
            var shown = WebhookSigning.WithoutSecret(before.Type, storedParameters);
            var unchanged = SameParameters(WebhookSigning.WithoutSecret(next.Parameters), shown);

            foreach (var (name, value) in storedParameters)
            {
                if (!WebhookSigning.IsSensitiveParameterName(name)
                    || next.Parameters.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!unchanged)
                {
                    return ([], $"actions[{i}] ({before.Type}) changed, so its {name} is not kept. "
                        + "Send it again, or send it blank to clear it.");
                }

                kept.Add((i, name, value));
            }

            if (!unchanged)
            {
                continue;
            }

            // A Conditional's branches are shown with their children's credentials left out, so an
            // unchanged branch is put back as stored, credentials and all.
            foreach (var (name, value) in shown)
            {
                if (storedParameters.TryGetValue(name, out var original) && original != value)
                {
                    kept.Add((i, name, original));
                }
            }
        }

        return (kept, null);
    }

    private static bool SameParameters(Dictionary<string, string> sent, Dictionary<string, string> shown) =>
        sent.Count == shown.Count
        && sent.All(p => shown.TryGetValue(p.Key, out var value) && value == p.Value);
}
