using System.Security.Claims;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// The single place an access token is minted.
///
/// Every path that issues a token has to answer the same question first — <em>may this user hold a
/// token for this tenant at all?</em> — and it has to answer it identically. When each endpoint
/// asked on its own, three of the four forgot to ask: login, OTP verify and refresh all trusted the
/// resolved tenant (which comes from a client-supplied <c>X-Tenant</c> header) and minted a
/// <c>tenant</c> claim for it. Because role resolution falls back to the user's global roles when no
/// membership exists, any registered user could sign in against any tenant and receive a usable
/// token for it.
///
/// Routing all four through here means the check cannot be skipped by omission: there is no way to
/// get a token without going past <see cref="IssueAccessTokenAsync"/>.
/// </summary>
public interface ITokenIssuer
{
    /// <summary>
    /// Mints an access token scoped to <paramref name="tenantSlug"/>, or returns
    /// <see cref="TokenIssueResult.Denied"/> when the user has no claim to that tenant.
    ///
    /// Callers must treat a denied result as an authorization failure. Do not fall back to issuing a
    /// token for another tenant — that is the bug this type exists to prevent.
    /// </summary>
    /// <param name="user">The authenticated user. Authentication is the caller's job; this decides scope only.</param>
    /// <param name="tenantSlug">The tenant the token should be scoped to.</param>
    /// <param name="extraClaims">
    /// Additional claims to carry, e.g. DeviceTrust's device binding. Reserved claim names
    /// (<c>jti</c>, <c>UserId</c>, <c>Username</c>, <c>tenant</c>, role) are set here and cannot be
    /// overridden by this collection.
    /// </param>
    Task<TokenIssueResult> IssueAccessTokenAsync(
        User user,
        string tenantSlug,
        IEnumerable<Claim>? extraClaims = null,
        CancellationToken ct = default);
}

/// <summary>Outcome of a token request. Check <see cref="Allowed"/> before using <see cref="Token"/>.</summary>
public sealed record TokenIssueResult
{
    private TokenIssueResult() { }

    /// <summary>False when the user may not hold a token for the requested tenant.</summary>
    public bool Allowed { get; private init; }

    /// <summary>The signed JWT. Empty when <see cref="Allowed"/> is false.</summary>
    public string Token { get; private init; } = string.Empty;

    /// <summary>The token's JWT ID, needed by callers that track revocation.</summary>
    public string Jti { get; private init; } = string.Empty;

    public DateTime ExpiresAt { get; private init; }

    /// <summary>Role names embedded in the token, for logging.</summary>
    public IReadOnlyList<string> Roles { get; private init; } = Array.Empty<string>();

    /// <summary>Why the request was denied. For logs — do not return this to the caller verbatim.</summary>
    public string? DenialReason { get; private init; }

    public static TokenIssueResult Granted(
        string token, string jti, DateTime expiresAt, IReadOnlyList<string> roles) =>
        new() { Allowed = true, Token = token, Jti = jti, ExpiresAt = expiresAt, Roles = roles };

    public static TokenIssueResult Denied(string reason) =>
        new() { Allowed = false, DenialReason = reason };
}
