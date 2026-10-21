using System.Security.Claims;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Content.Batch;

/// <summary>
/// POST /api/contents/batch: creates and lifecycle transitions in one database transaction.
/// </summary>
/// <remarks>
/// Every step is held to the checks of the single create and the status change (see
/// <see cref="BatchSteps"/>), and the first refusal rolls back the whole batch, so a client never
/// holds half of one. An <c>Idempotency-Key</c> applies as on any other write: a retry of a
/// batch that was answered 200 gets the stored answer and runs nothing.
/// </remarks>
internal sealed class Endpoint(IContentBatchRunner batch, IDocumentSession session) : Endpoint<BatchRequest, BatchResponse>
{
    public override void Configure()
    {
        Post("/api/contents/batch");
        Claims("UserId");
        Summary(s => s.Summary = "Create entries and run transitions in one transaction.");
    }

    public override async Task HandleAsync(BatchRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("UserId"), out var userId)
            || await session.LoadAsync<barakoCMS.Models.User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var outcome = await batch.RunAsync(async (scope, token) =>
        {
            var result = await BatchSteps.RunAsync(scope, req, user, HttpContext, token);
            return new ContentBatchOutcome<BatchOutcome>(result, result.Failure is null);
        }, ct);

        if (outcome.Failure is { } failure)
        {
            AddError(new FluentValidation.Results.ValidationFailure($"steps[{failure.Index}]", failure.Reason));
            await Send.ErrorsAsync(failure.Status, ct);
            return;
        }

        await Send.OkAsync(new BatchResponse(outcome.Steps), ct);
    }
}
