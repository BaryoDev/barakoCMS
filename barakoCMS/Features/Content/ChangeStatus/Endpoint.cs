using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using System.Security.Claims;

namespace barakoCMS.Features.Content.ChangeStatus;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IContentWriter contentWriter,
    IContentTransitioner transitioner) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/contents/{id}/status");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

        var user = await session.LoadAsync<barakoCMS.Models.User>(userId, ct);

        var content = await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user == null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Which of the two request shapes is correct depends on the content type, which the
        // validator cannot see. A type with a lifecycle takes a named transition; every type that
        // exists today has none and takes a status.
        var definition = await session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);
        var lifecycle = definition?.Lifecycle;

        // The permission check is deliberately not shared between the two paths.
        //
        // A status change is an edit, so it checks Update, which is what it has always done. A
        // transition is not: the whole point of #341 is that a manager approves an amount they may
        // not edit, so requiring Update as well would make the interesting half of separation of
        // duties unreachable. Checking both would have looked more careful and been strictly worse.
        //
        // The transition check lives further in, because which permission applies depends on which
        // transition was named and that is only known once the request has been matched against the
        // lifecycle.
        if (lifecycle is not null)
        {
            await HandleTransitionAsync(req, content, userId, ct);
            return;
        }

        if (req.Transition is not null)
        {
            ThrowError($"Content type '{content.ContentType}' declares no lifecycle, so it takes NewStatus rather than a transition.", 400);
        }

        // A status change is an edit of the entry, so it is governed by Update, unchanged.
        if (!await permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var newStatus = req.NewStatus!.Value;

        // A no-op request appends nothing. ContentStatusChanged carries only the new status, so the
        // workflow projection cannot tell "moved to Published" from "set to Published while already
        // Published": a second event fires every Published workflow again, and the confirmation
        // email goes out twice for a double-clicked button or a client retry. The Update slice has
        // always guarded this; this one did not. It also keeps transitions that changed nothing out
        // of the stream, which is the source of truth for history and replay.
        if (content.Status == newStatus)
        {
            await Send.ResponseAsync(new Response
            {
                Message = $"Content status is already {newStatus}"
            });
            return;
        }

        var @event = new barakoCMS.Events.ContentStatusChanged(req.Id, newStatus, userId, DateTime.UtcNow);

        // Append the event AND update the read-model document in one transaction so they can't
        // diverge. Workflows fire out-of-band via the async WorkflowProjection, which is driven off the
        // event stream — so the append is what makes "Published" workflows actually run.
        //
        // Under an expected-version check rather than a plain append: this is a whole-document write
        // built from a document loaded at the top of the request, so an unguarded append would let it
        // overwrite a scheduler transition or an edit that landed in between.
        try
        {
            await contentWriter.AppendOptimisticAsync(content, new[] { @event }, ct);

            // There's no content-delete endpoint in barakoCMS today, and archiving is the closest
            // destructive-equivalent action, so it's what gets audited here rather than every routine
            // draft-to-published transition, which would just be noise.
            if (newStatus == barakoCMS.Models.ContentStatus.Archived)
            {
                await AuditLog.RecordAsync(session, tenant.Slug, "content.archived", userId, user.Username,
                    targetType: content.ContentType, targetId: content.Id.ToString(), ct: ct);
            }

            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            // 409 rather than the 412 the update endpoint returns: nothing here was conditional on a
            // version the client sent, so there is no precondition to have failed.
            ThrowError("The content was changed by another writer. Please refresh and try again.", 409);
        }

        await Send.ResponseAsync(new Response
        {
            Message = $"Content status changed to {newStatus}"
        });
    }

    /// <summary>
    /// Hands a named transition to <see cref="IContentTransitioner"/> and answers with its result.
    /// </summary>
    /// <remarks>
    /// The rules live in the transitioner, so a move made by a job or a module is held to the ones
    /// a request is. What stays here is the request's shape and the status code for each outcome.
    /// A refusal for lack of permission answers 403 with no body: the reason the transitioner gives
    /// is for calling code, and a caller without the right is told nothing about the type's
    /// transitions or the entry's state.
    /// </remarks>
    private async Task HandleTransitionAsync(
        Request req,
        barakoCMS.Models.Content content,
        Guid userId,
        CancellationToken ct)
    {
        if (req.NewStatus.HasValue)
        {
            ThrowError($"Content type '{content.ContentType}' declares a lifecycle, so it takes Transition rather than NewStatus.", 400);
        }

        var result = await transitioner.TransitionAsync(
            content,
            req.Transition!,
            ContentTransitionActor.ForUser(userId),
            new ContentTransitionOptions { Data = req.Data },
            ct);

        switch (result.Outcome)
        {
            case ContentTransitionOutcome.Forbidden:
                await Send.ForbiddenAsync(ct);
                return;

            case ContentTransitionOutcome.Conflict:
                ThrowError(result.Errors[0], 409);
                return;

            case ContentTransitionOutcome.Invalid:
                foreach (var error in result.Errors)
                {
                    AddError(error);
                }

                ThrowIfAnyErrors();
                return;
        }

        await Send.ResponseAsync(new Response
        {
            Message = $"{result.Transition} moved this entry to {result.ToState}",
        });
    }
}
