using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ContentEntry = barakoCMS.Models.Content;

namespace barakoCMS.Features.Content.Batch;

/// <summary>Why a step refused the batch: the step, the status to answer with, and the text.</summary>
internal sealed record BatchFailure(int Index, int Status, string Reason);

internal sealed record BatchOutcome(List<BatchStepResult> Steps, BatchFailure? Failure);

/// <summary>
/// Runs a batch's steps in order inside the open batch transaction, each one through the same
/// checks the single create and the status change run.
/// </summary>
/// <remarks>
/// A step is checked as its own request would be: the create permission on the type, write
/// sensitivity, the create rule on the data the caller may write and again on the entry as stored,
/// the type's save rules, validation and hooks; and for a transition the read floor, the transition
/// permission, the self transition rule, the declared fields and the transition rules. The batch
/// only decides what happens around them: each step is flushed so the next reads it, and the first
/// refusal stops the run, so nothing is committed.
///
/// A refusal that says what a rule read goes only to a caller whose create rule holds for the
/// entry, as it does on the single create. A permission refusal carries no detail for the same
/// reason the status route answers 403 with no body.
/// </remarks>
internal sealed class BatchSteps(IServiceProvider scope, User user, HttpContext http)
{
    private const string Forbidden = "Forbidden.";

    private readonly IDocumentSession _session = scope.GetRequiredService<IDocumentSession>();
    private readonly IPermissionResolver _permissions = scope.GetRequiredService<IPermissionResolver>();
    private readonly HashSet<Guid> _ids = [];

    public static async Task<BatchOutcome> RunAsync(
        IServiceProvider scope, BatchRequest request, User user, HttpContext http, CancellationToken ct)
    {
        // A move of an entry made earlier in the batch needs that entry's stream to exist. The
        // scope ends with the run, so the flag is off again whatever the run does.
        using var eager = MovesAnEntryItCreated(request)
            ? scope.GetRequiredService<Infrastructure.Multitenancy.BatchTransaction>().EagerStreamsScope()
            : null;

        var run = new BatchSteps(scope, user, http);
        var results = new List<BatchStepResult>();

        for (var index = 0; index < request.Steps.Count; index++)
        {
            var step = request.Steps[index];
            var (result, failure) = await run.StepAsync(index, step, ct);
            if (failure is not null)
                return new BatchOutcome(results, failure);

            results.Add(result!);
        }

        return new BatchOutcome(results, null);
    }

    internal static ContentStatus? ParseStatus(string value) =>
        Enum.TryParse<ContentStatus>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    internal static SensitivityLevel? ParseSensitivity(string value) =>
        Enum.TryParse<SensitivityLevel>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static bool MovesAnEntryItCreated(BatchRequest request)
    {
        var created = new HashSet<Guid>();
        foreach (var step in request.Steps)
        {
            if (BatchValidator.IsOp(step.Op, BatchOps.Create))
                created.Add(step.Id);
            else if (created.Contains(step.Id))
                return true;
        }

        return false;
    }

    private async Task<(BatchStepResult? Result, BatchFailure? Failure)> StepAsync(
        int index, BatchStep step, CancellationToken ct)
    {
        // A client id names one entry for the whole batch, so a create cannot overwrite one an
        // earlier step wrote, and a move cannot be confused with a create of the same id.
        if (BatchValidator.IsOp(step.Op, BatchOps.Create) && !_ids.Add(step.Id))
            return (null, new BatchFailure(index, 400, "Id was already used in this batch."));

        return BatchValidator.IsOp(step.Op, BatchOps.Create)
            ? await CreateStepAsync(index, step, ct)
            : await TransitionStepAsync(index, step, ct);
    }

    private async Task<(BatchStepResult? Result, BatchFailure? Failure)> CreateStepAsync(
        int index, BatchStep step, CancellationToken ct)
    {
        if (!await _permissions.CanPerformActionAsync(user, step.ContentType, "create", null, ct))
            return (null, new BatchFailure(index, 403, Forbidden));

        var creator = scope.GetRequiredService<IContentCreator>();
        var request = new ContentCreateRequest
        {
            ContentType = step.ContentType,
            Data = step.Data ?? new Dictionary<string, object>(),
            Id = step.Id,
            Status = step.Status is null ? ContentStatus.Draft : ParseStatus(step.Status)!.Value,
            Sensitivity = step.Sensitivity is null ? SensitivityLevel.Public : ParseSensitivity(step.Sensitivity)!.Value,
            // Asked of the data the caller may write before any save rule runs, as the single create does.
            Permitted = (entry, token) => _permissions.AllowsCreatedEntryAsync(user, entry, token),
        };

        // A stored entry under this id would be overwritten by the document write.
        if (await _session.LoadAsync<ContentEntry>(step.Id, ct) is not null)
            return (null, new BatchFailure(index, 409, "Id is already in use."));

        var errors = await creator.CheckAsync(request, user.Id, http, batch: null, ct);
        if (request.Forbidden)
            return (null, new BatchFailure(index, 403, Forbidden));

        if (errors.Count > 0)
            return (null, await RefusalAsync(index, request, errors, ct));

        // The check above asks only whether a Create rule exists. This asks whether one holds for the
        // entry as it will be stored.
        if (!await _permissions.AllowsCreatedEntryAsync(user, request, ct))
            return (null, new BatchFailure(index, 403, Forbidden));

        return await StageAsync(index, step, request, creator, ct);
    }

    private async Task<(BatchStepResult? Result, BatchFailure? Failure)> StageAsync(
        int index, BatchStep step, ContentCreateRequest request, IContentCreator creator, CancellationToken ct)
    {
        try
        {
            var created = await creator.StageAsync(request, user.Id, batch: null, ct);

            // Written now, so the next step's checks and hooks read this entry.
            await _session.SaveChangesAsync(ct);
            return (new BatchStepResult(index, BatchOps.Create, created.Id, created.LifecycleState), null);
        }
        catch (ContentUniquenessException ex)
        {
            return (null, new BatchFailure(index, 409, ex.Message));
        }
        catch (ContentCreateRefusedException ex)
        {
            return (null, new BatchFailure(index, 400, ex.Message));
        }
    }

    /// <summary>
    /// The failure for a create the checks refused: 422 for a save rule, 400 otherwise. A rule may
    /// read other entries, so any of these goes only to a caller whose create rule holds for this
    /// entry. Anyone else gets 403.
    /// </summary>
    private async Task<BatchFailure> RefusalAsync(
        int index, ContentCreateRequest request, IReadOnlyList<string> errors, CancellationToken ct)
    {
        if (!await _permissions.AllowsCreatedEntryAsync(user, request, ct))
            return new BatchFailure(index, 403, Forbidden);

        return request.RuleFailure is { } refused
            ? new BatchFailure(index, 422, refused)
            : new BatchFailure(index, 400, string.Join(" ", errors));
    }

    private async Task<(BatchStepResult? Result, BatchFailure? Failure)> TransitionStepAsync(
        int index, BatchStep step, CancellationToken ct)
    {
        var content = await _session.LoadAsync<ContentEntry>(step.Id, ct);
        if (content is null)
            return (null, new BatchFailure(index, 404, "Entry not found."));

        var result = await scope.GetRequiredService<IContentTransitioner>().TransitionAsync(
            content, step.Transition!, ContentTransitionActor.ForUser(user.Id),
            new ContentTransitionOptions { Data = step.Data }, ct);

        return result.Outcome switch
        {
            ContentTransitionOutcome.Transitioned =>
                (new BatchStepResult(index, BatchOps.Transition, content.Id, result.ToState), null),
            ContentTransitionOutcome.Forbidden => (null, new BatchFailure(index, 403, Forbidden)),
            ContentTransitionOutcome.Conflict => (null, new BatchFailure(index, 409, result.Errors[0])),
            ContentTransitionOutcome.RuleFailed => (null, new BatchFailure(index, 422, result.Errors[0])),
            ContentTransitionOutcome.Invalid => (null, new BatchFailure(index, 400, string.Join(" ", result.Errors))),
            _ => throw new InvalidOperationException(
                $"The transitioner answered {result.Outcome}, which a batch cannot turn into a response."),
        };
    }
}
