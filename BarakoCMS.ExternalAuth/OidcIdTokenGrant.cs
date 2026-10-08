using System.Security.Cryptography;
using System.Text;
using FastEndpoints;
using FluentValidation;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// A nonce the id token grant has accepted, kept until the token it came in has expired so the
/// same token, or another one carrying the same nonce, cannot be exchanged again.
/// </summary>
/// <remarks>Global, like <see cref="ExternalIdentity"/>. The id is a hash of issuer and nonce.</remarks>
public sealed class OidcUsedNonce
{
    public string Id { get; set; } = string.Empty;

    /// <summary>When the token expired, plus the clock skew it was validated with. UTC.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>SHA-256 of issuer and nonce, each prefixed with its length, like <see cref="ExternalIdentity.KeyOf"/>.</summary>
    internal static string KeyOf(string issuer, string nonce) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{issuer.Length}:{issuer}|{nonce.Length}:{nonce}")));
}

internal sealed class OidcIdTokenGrantRequest
{
    /// <summary>The id token the app got from the provider's own SDK.</summary>
    public string IdToken { get; set; } = string.Empty;

    /// <summary>The nonce the app gave the provider, exactly as it appears in the token.</summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>The club to sign in to. The default one when empty.</summary>
    public string? Club { get; set; }
}

internal sealed class OidcIdTokenGrantResponse
{
    public string Token { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>True when the account has MFA enrolled: no tokens, finish at /api/auth/mfa/verify.</summary>
    public bool RequiresMfa { get; set; }

    public string? MfaChallengeToken { get; set; }
}

internal sealed class OidcIdTokenGrantValidator : Validator<OidcIdTokenGrantRequest>
{
    /// <summary>Printable ASCII with no space, 16 to 256 characters: room for a hash or a random string.</summary>
    internal const string NoncePattern = "^[\\x21-\\x7E]{16,256}$";

    public OidcIdTokenGrantValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty().MaximumLength(OidcIdToken.MaxLength);
        RuleFor(x => x.Nonce).NotEmpty().Matches(NoncePattern)
            .WithMessage("Nonce must be 16 to 256 printable characters with no spaces.");
        RuleFor(x => x.Club).MaximumLength(OidcSupport.MaxClubLength);
    }
}

/// <summary>
/// POST /api/auth/oidc/{name}/id-token. A native app that already holds an id token from the
/// provider's own SDK exchanges it for a barako token, with no browser redirect.
/// </summary>
/// <remarks>
/// <para>
/// Off unless the provider lists <c>IdTokenAudiences</c>: a native app's client id is not the web
/// one, and Google's native tokens name the server's client as <c>aud</c> and the app's as
/// <c>azp</c>. The token is checked as the callback checks it (issuer, signature against the
/// issuer's keys, audience, expiry, nonce, <c>email_verified</c>), with the configured audiences in
/// place of the client id.
/// </para>
/// <para>
/// The nonce is required, and is what makes a token usable once. Nothing ties the token to this
/// caller the way the state cookie ties a redirect to a browser, so a token that leaks from the app
/// could otherwise be exchanged by whoever has it for as long as it lives. A nonce is recorded when
/// it is accepted and refused again until the token it came in has expired. The record is a unique
/// insert, so two requests racing with one token get one sign-in. A token whose expiry is more than
/// <see cref="MaxTokenLifetime"/> away is refused, which bounds how long a record is kept.
/// </para>
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class OidcIdTokenGrantEndpoint(
    IDocumentSession session,
    IDocumentStore store,
    IConfiguration config,
    OidcBackchannel backchannel,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
    ILogger<OidcIdTokenGrantEndpoint> logger) : Endpoint<OidcIdTokenGrantRequest, OidcIdTokenGrantResponse>
{
    internal static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(24);

    public override void Configure()
    {
        Post("/api/auth/oidc/{name}/id-token");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(OidcSupport.RateLimitPolicy));
    }

    public override async Task HandleAsync(OidcIdTokenGrantRequest req, CancellationToken ct)
    {
        var provider = OidcProviders.Find(config, Route<string>("name"));
        if (provider is null || provider.IdTokenAudiences.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var endpoints = await backchannel.EndpointsAsync(provider, ct);
        if (endpoints is null)
        {
            ThrowError($"Couldn't reach {provider.DisplayName}. Please try again.", 503);
        }

        var keys = await backchannel.SigningKeysAsync(provider, endpoints, OidcIdToken.KeyIdOf(req.IdToken), ct);
        var (identity, refusal) = await OidcIdToken.ValidateAsync(
            req.IdToken, provider, keys, req.Nonce, provider.IdTokenAudiences);

        var expiresAt = identity is null ? default : new JsonWebTokenHandler().ReadJsonWebToken(req.IdToken).ValidTo;
        if (identity is not null && expiresAt > DateTime.UtcNow + MaxTokenLifetime)
        {
            (identity, refusal) = (null, OidcRefusal.Lifetime);
        }

        if (identity is null)
        {
            // The reason is one of a fixed set of names. Nothing from the token is logged.
            logger.LogWarning("OIDC provider {Provider}: an id token grant was refused ({Refusal})", provider.Name, refusal);
            ThrowError($"The {provider.DisplayName} id token could not be verified.", 401);
        }

        if (!await TryUseNonceAsync(identity.Issuer, req.Nonce, expiresAt + OidcIdToken.ClockSkew, ct))
        {
            logger.LogWarning("OIDC provider {Provider}: an id token grant reused a nonce", provider.Name);
            ThrowError("This nonce was already used. Sign in with the provider again.", 401);
        }

        var club = (req.Club ?? string.Empty).Trim().ToLowerInvariant();
        var mfa = Resolve<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>();
        SocialSignIn.Tokens tokens;
        bool emailNotVerified;
        try
        {
            (tokens, emailNotVerified) = await SocialSignIn.IssueForIdentityAsync(
                session, config, deviceGate, tokenIssuer, mfa, HttpContext, identity, provider.Name, club, ct);
        }
        catch (Exception ex) when (OidcSupport.IsUniqueViolation(ex))
        {
            // Two first sign-ins for one address at once. The loser tries again and finds the account.
            ThrowError("Another sign-in for this account is in progress. Please try again.", 409);
            return;
        }

        if (emailNotVerified)
        {
            ThrowError($"{provider.DisplayName} didn't share a verified email address.", 401);
        }

        if (tokens.RequiresMfa)
        {
            await Send.OkAsync(new OidcIdTokenGrantResponse { RequiresMfa = true, MfaChallengeToken = tokens.MfaChallenge }, ct);
            return;
        }

        if (!tokens.Allowed)
        {
            ThrowError("You are not a member of this club.", 403);
        }

        await Send.OkAsync(new OidcIdTokenGrantResponse { Token = tokens.Token, RefreshToken = tokens.Refresh }, ct);
    }

    /// <summary>
    /// Records the nonce, or says it is already recorded and live. Expired records go in the same
    /// commit, so one whose token has expired no longer blocks its nonce, and the table holds only
    /// what is live.
    /// </summary>
    private async Task<bool> TryUseNonceAsync(string issuer, string nonce, DateTime until, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var claim = store.LightweightSession();
        claim.DeleteWhere<OidcUsedNonce>(n => n.ExpiresAt <= now);
        claim.Insert(new OidcUsedNonce { Id = OidcUsedNonce.KeyOf(issuer, nonce), ExpiresAt = until });
        try
        {
            await claim.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (OidcSupport.IsUniqueViolation(ex))
        {
            return false;
        }
    }
}
