using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Workflows.ActionLinks.Preview;

internal sealed class Request
{
    public string Token { get; set; } = string.Empty;
}

internal sealed class Response
{
    public Guid EntryId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string Transition { get; set; } = string.Empty;
    public string FromState { get; set; } = string.Empty;
    public string ToState { get; set; } = string.Empty;

    /// <summary>The fields the confirmation must send, for example a note.</summary>
    public List<string> RequiredFields { get; set; } = [];

    /// <summary>The fields the confirmation may send.</summary>
    public List<string> OptionalFields { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// GET /api/public/action-links/{token}: what confirming the link would do. Changes nothing, so a
/// mail scanner that follows the link approves nothing.
/// </summary>
/// <remarks>
/// 404 for a bad, expired, spent or revoked link, and for a user who may no longer make the move.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    ActionLinkTokenIssuer tokens,
    IPermissionResolver permissions) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/public/action-links/{token}");
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

        await Send.OkAsync(new Response
        {
            EntryId = open.Content.Id,
            ContentType = open.Content.ContentType,
            Transition = open.Transition.Name,
            FromState = open.Transition.From,
            ToState = open.Transition.To,
            RequiredFields = [.. open.Transition.RequiredFields],
            OptionalFields = [.. open.Transition.OptionalFields ?? []],
            ExpiresAt = open.Claims.ExpiresAt,
        }, ct);
    }
}
