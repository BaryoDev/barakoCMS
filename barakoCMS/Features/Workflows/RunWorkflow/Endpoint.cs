using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Filters;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Workflows.RunWorkflow;

internal sealed class Request
{
    public Guid Id { get; set; }

    /// <summary>The entry to run the workflow on.</summary>
    public Guid EntryId { get; set; }
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.EntryId).NotEmpty().WithMessage("entryId is required.");
    }
}

internal sealed class Response
{
    public Guid RunId { get; init; }

    /// <summary>True when an <c>Idempotency-Key</c> found the run an earlier request started.</summary>
    public bool Repeated { get; init; }
}

/// <summary>
/// POST /api/workflows/{id}/run. Starts one workflow on one entry, as a run with trigger Manual.
/// </summary>
/// <remarks>
/// Queues and returns, like every other way a run starts: the runner does the work. The workflow's
/// conditions are checked and not bypassed. Having a run start past them is a different feature.
///
/// Reads <c>Idempotency-Key</c> itself. The run id is derived from the tenant, the workflow, the
/// entry and the key, so a repeat finds the run and the same key on another workflow or entry is
/// another run. The global idempotency filter would answer that case with a 422, so it is told to
/// leave this route alone.
///
/// An entry the caller cannot read is a 404, as an entry that does not exist is: the id of a hidden
/// entry should not be confirmable by trying to run something on it.
/// </remarks>
[ReadsOwnIdempotencyKey]
internal sealed class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    IPermissionResolver permissions,
    ISensitivityService sensitivity,
    IWorkflowRunQueue queue,
    TenantContext tenant) : Endpoint<Request, Response>
{
    public const string KeyHeader = "Idempotency-Key";

    public override void Configure()
    {
        Post("/api/workflows/{id}/run");
        Definition.RequireCapability(SystemCapabilities.RunWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Under the lock a delete of the workflow takes, so a run is not queued for a workflow the
        // delete has already looked past. It also makes two requests with one key take turns.
        await WorkflowDefinitionLock.TakeAsync(session, req.Id, ct);

        var workflow = await session.LoadAsync<WorkflowDefinition>(req.Id, ct);
        if (workflow is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entry = await session.LoadAsync<barakoCMS.Models.Content>(req.EntryId, ct);
        if (entry is null
            || !await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct)
            || await HiddenFromCallerAsync(entry, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!WorkflowTriggers.ContentTypes(workflow).Contains(entry.ContentType, StringComparer.Ordinal))
        {
            ThrowError($"That workflow does not run on entries of type {entry.ContentType}.", 400);
            return;
        }

        var key = HttpContext.Request.Headers[KeyHeader].ToString();
        Guid? runId = string.IsNullOrWhiteSpace(key) ? null : RunIdFor(tenant.Slug, workflow.Id, entry.Id, key.Trim());

        // Before the workflow is judged again: a repeat of a request that worked must find its run
        // even if the entry has changed or the workflow been switched off since.
        if (runId is { } known && await session.LoadAsync<WorkflowRun>(known, ct) is not null)
        {
            await Send.ResponseAsync(new Response { RunId = known, Repeated = true }, 200, ct);
            return;
        }

        if (!workflow.Enabled)
        {
            ThrowError("That workflow is switched off. Switch it on first, then run it.", 409);
            return;
        }

        if (Mismatch(workflow, entry) is { } reason)
        {
            ThrowError($"That entry does not meet the workflow's conditions: {reason}", 409);
            return;
        }

        var run = queue.StageManual(workflow, entry, runId, userId);
        await WorkflowAudit.RecordAsync(session, tenant.Slug, "workflow.run.manual", workflow, User,
            new Dictionary<string, object> { ["runId"] = run.Id, ["entryId"] = entry.Id }, ct);

        await SaveAndAnswerAsync(run, runId, ct);
    }

    private async Task SaveAndAnswerAsync(WorkflowRun run, Guid? runId, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (runId is not null && IdempotencyFilter.IsUniqueViolation(ex))
        {
            // Another node saved the same key between the check above and here. Its run is the answer.
            await using var read = store.QuerySession(session.TenantId);
            if (await read.LoadAsync<WorkflowRun>(runId.Value, ct) is not null)
            {
                await Send.ResponseAsync(new Response { RunId = runId.Value, Repeated = true }, 200, ct);
                return;
            }

            throw;
        }

        WorkflowMetrics.Default.Queued(WorkflowEvents.Manual, 1);
        await Send.ResponseAsync(new Response { RunId = run.Id }, 202, ct);
    }

    private async Task<bool> HiddenFromCallerAsync(barakoCMS.Models.Content entry, CancellationToken ct) =>
        await sensitivity.ApplyAsync(entry, new Dictionary<string, object>(entry.Data ?? []), HttpContext, ct);

    /// <summary>The first condition the entry fails, in the words the run list uses, or null when it meets them all.</summary>
    private static string? Mismatch(WorkflowDefinition workflow, barakoCMS.Models.Content entry)
    {
        foreach (var (field, value) in workflow.Conditions ?? [])
        {
            var single = new WorkflowDefinition { Conditions = new Dictionary<string, string> { [field] = value } };
            if (!WorkflowConditions.Matches(single, entry)) return $"{field} must be {value}.";
        }

        return null;
    }

    internal static Guid RunIdFor(string tenantSlug, Guid workflowId, Guid entryId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantSlug}\u001f{workflowId:N}\u001f{entryId:N}\u001f{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
