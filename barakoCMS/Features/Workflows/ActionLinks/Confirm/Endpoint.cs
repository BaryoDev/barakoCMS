using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Workflows.ActionLinks.Confirm;

internal sealed class Request
{
    public string Token { get; set; } = string.Empty;

    /// <summary>Values for the fields the transition declares, such as a note.</summary>
    public Dictionary<string, object>? Data { get; set; }
}

internal sealed class Response
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/public/action-links/{token}: makes the transition the link is for, as the user it is
/// for, and spends the link.
/// </summary>
/// <remarks>
/// The permission and row conditions are checked by the transitioner at this moment, not read from
/// the link. The mark that spends the link is stored in the same commit as the transition, so a
/// refused move leaves the link usable and a second use finds it spent. A bad, expired, spent or
/// revoked link, and a user who may no longer make the move, all answer 404.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(
    IDocumentSession session,
    ActionLinkTokenIssuer tokens,
    IPermissionResolver permissions,
    IContentTransitioner transitioner) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/public/action-links/{token}");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.SiteSharePolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";

        var open = await ActionLinkGate.OpenAsync(session, tokens, permissions, req.Token, ct);
        if (open is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Insert(new ActionLinkUse
        {
            Key = open.Claims.Id.ToString("N"),
            ConsumedAt = DateTimeOffset.UtcNow,
            ExpiresAt = open.Claims.ExpiresAt,
        });

        ContentTransitionResult result;
        try
        {
            result = await transitioner.TransitionAsync(
                open.Content,
                open.Transition.Name,
                ContentTransitionActor.ForUser(open.User.Id),
                new ContentTransitionOptions { Data = req.Data },
                ct);
        }
        catch (JasperFx.DocumentAlreadyExistsException)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        switch (result.Outcome)
        {
            case ContentTransitionOutcome.Transitioned:
                await Send.OkAsync(new Response
                {
                    Message = $"{result.Transition} moved this entry to {result.ToState}",
                }, ct);
                return;

            case ContentTransitionOutcome.Forbidden:
                await Send.NotFoundAsync(ct);
                return;

            case ContentTransitionOutcome.Conflict:
                ThrowError(result.Errors[0], 409);
                return;

            default:
                foreach (var error in result.Errors)
                {
                    AddError(error);
                }

                ThrowIfAnyErrors();
                return;
        }
    }
}
