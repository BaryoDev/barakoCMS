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

        // Stored as the content type declares it. The engine matches this with an equality query, so
        // a workflow saved as "transition:approve" against a transition named "Approve" would be
        // accepted here and then never fire.
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
