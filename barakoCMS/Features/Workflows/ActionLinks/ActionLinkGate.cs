using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.ActionLinks;

/// <summary>An action link that still holds: its entry, the declared transition and the user it is for.</summary>
internal sealed record OpenActionLink(
    ActionLinkClaims Claims, barakoCMS.Models.Content Content, StateTransition Transition, User User);

/// <summary>
/// Decides whether a signed action link can still be acted on. Every refusal is the same null, so
/// a caller of the public routes learns nothing about which check failed.
/// </summary>
/// <remarks>
/// Runs on the preview and again on the confirmation, at the moment of use: the link is revoked
/// when it was spent, when the entry left the state it was issued in, or when the user no longer
/// holds the transition's permission on this entry (a lost role, or a row condition that no longer
/// matches).
/// </remarks>
internal static class ActionLinkGate
{
    public static async Task<OpenActionLink?> OpenAsync(
        IDocumentSession session,
        ActionLinkTokenIssuer tokens,
        IPermissionResolver permissions,
        string? token,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (tokens.Read(token, now) is not { } claims)
            return null;

        if (await session.LoadAsync<ActionLinkUse>(claims.Id.ToString("N"), ct) is not null)
            return null;

        var content = await session.LoadAsync<barakoCMS.Models.Content>(claims.ContentId, ct);
        if (content is null)
            return null;

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);
        if (definition?.Lifecycle is not { } lifecycle)
            return null;

        var state = content.LifecycleState ?? lifecycle.InitialState;
        if (!string.Equals(state, claims.FromState, StringComparison.OrdinalIgnoreCase))
            return null;

        var declared = lifecycle.Transitions.FirstOrDefault(
            t => string.Equals(t.Name, claims.Transition, StringComparison.OrdinalIgnoreCase));
        if (declared is null || !string.Equals(declared.From, state, StringComparison.OrdinalIgnoreCase))
            return null;

        var user = await session.LoadAsync<User>(claims.UserId, ct);
        if (user is null)
            return null;

        var allowed = await permissions.CanPerformActionAsync(
            user, content.ContentType, PermissionResolver.TransitionActionPrefix + declared.Name, content, ct);

        return allowed ? new OpenActionLink(claims, content, declared, user) : null;
    }
}
