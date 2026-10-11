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
/// it is accepted and refused again until the token it came in has expired. The record commits with
/// the sign-in, so a sign-in that fails leaves the token usable for a retry, and it is a unique
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
    OidcUsedNonces nonces,
    OidcGrantSignIn signIn,
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

        await nonces.PurgeIfDueAsync(store, ct);

        var nonceKey = OidcUsedNonce.KeyOf(identity.Issuer, req.Nonce);
        if (await nonces.IsLiveAsync(store, nonceKey, ct))
        {
            RefuseReplay(provider);
        }

        var club = (req.Club ?? string.Empty).Trim().ToLowerInvariant();
        var mfa = Resolve<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>();
        SocialSignIn.Tokens tokens;
        bool emailNotVerified;
        try
        {
            // The nonce is queued just before the commit that records the outcome (the tokens, the
            // MFA challenge or the refusal), so it is spent with that and with nothing earlier, and a
            // sign-in that fails before it leaves the token usable. The insert is also the replay
            // check: two grants racing with one token both get here, and the primary key lets one
            // commit.
            (tokens, emailNotVerified) = await signIn.IssueAsync(
                session, config, deviceGate, tokenIssuer, mfa, HttpContext, identity, provider.Name, club, ct,
                beforeFinalCommit: s => nonces.QueueUse(s, nonceKey, expiresAt + OidcIdToken.ClockSkew));
        }
        catch (Exception ex) when (OidcSupport.IsUniqueViolation(ex))
        {
            // A commit failed, and with it the nonce. Either another grant spent the nonce first, or
            // two first sign-ins for one address raced on the user's unique email; the loser of that
            // retries with the same token and finds the account.
            if (await nonces.IsLiveAsync(store, nonceKey, ct))
            {
                RefuseReplay(provider);
            }

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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void RefuseReplay(OidcProvider provider)
    {
        logger.LogWarning("OIDC provider {Provider}: an id token grant reused a nonce", provider.Name);
        ThrowError("This nonce was already used. Sign in with the provider again.", 401);
    }
}

/// <summary>
/// The sign-in step of the id token grant. One instance per process; a test replaces it to make a
/// commit fail the way a lost race does, which a real race cannot be relied on to do.
/// </summary>
internal class OidcGrantSignIn
{
    public virtual Task<(SocialSignIn.Tokens Tokens, bool EmailNotVerified)> IssueAsync(
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa,
        Microsoft.AspNetCore.Http.HttpContext http,
        OidcIdentity identity,
        string provider,
        string club,
        CancellationToken ct,
        Action<IDocumentSession> beforeFinalCommit) =>
        SocialSignIn.IssueForIdentityAsync(
            session, config, deviceGate, tokenIssuer, mfa, http, identity, provider, club, ct, beforeFinalCommit);
}

/// <summary>The nonces the id token grant has spent: the replay check and the cleanup.</summary>
/// <remarks>
/// A record is live until its <see cref="OidcUsedNonce.ExpiresAt"/>. An expired one no longer blocks
/// its nonce: the use that finds it deletes it in the same commit as its own insert. Expired records
/// are otherwise removed by <see cref="PurgeIfDueAsync"/>, at most once per
/// <see cref="PurgeInterval"/> and at most <see cref="PurgeBatches"/> batches of
/// <see cref="PurgeBatchSize"/> each time, through the index on <c>ExpiresAt</c>. What a purge leaves
/// behind is logged and taken by the next one.
/// </remarks>
internal sealed class OidcUsedNonces(ILogger<OidcUsedNonces> logger)
{
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);
    internal const int PurgeBatchSize = 500;
    internal const int PurgeBatches = 10;

    private long _nextPurgeTicks;

    /// <summary>Replaced by tests that need records to expire without waiting.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Read through a session of its own, so the request's session tracks nothing for this id, and
    /// so it can still be asked after the request's session failed to commit.
    /// </summary>
    public async Task<bool> IsLiveAsync(IDocumentStore store, string key, CancellationToken ct)
    {
        await using var query = store.QuerySession();
        return await query.LoadAsync<OidcUsedNonce>(key, ct) is { } used && used.ExpiresAt > Now();
    }

    public void QueueUse(IDocumentSession session, string key, DateTime until)
    {
        var now = Now();
        session.DeleteWhere<OidcUsedNonce>(n => n.Id == key && n.ExpiresAt <= now);
        session.Insert(new OidcUsedNonce { Id = key, ExpiresAt = until });
    }

    /// <summary>Runs <see cref="PurgeAsync"/> when the interval has passed, on one request at a time.</summary>
    public async Task PurgeIfDueAsync(IDocumentStore store, CancellationToken ct)
    {
        var now = Now();
        var due = Interlocked.Read(ref _nextPurgeTicks);
        if (now.Ticks < due
            || Interlocked.CompareExchange(ref _nextPurgeTicks, (now + PurgeInterval).Ticks, due) != due)
        {
            return;
        }

        try
        {
            await PurgeAsync(store, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sign-in does not fail because the cleanup did. The next interval tries again.
            logger.LogWarning(ex, "Expired OIDC grant nonces could not be purged");
        }
    }

    /// <summary>Deletes expired records, a bounded number at a time. Returns how many went.</summary>
    internal async Task<int> PurgeAsync(IDocumentStore store, CancellationToken ct)
    {
        var now = Now();
        var removed = 0;
        for (var batch = 0; batch < PurgeBatches; batch++)
        {
            await using var session = store.LightweightSession();
            var expired = await session.Query<OidcUsedNonce>()
                .Where(n => n.ExpiresAt <= now)
                .Select(n => n.Id)
                .Take(PurgeBatchSize)
                .ToListAsync(ct);
            if (expired.Count == 0)
            {
                return removed;
            }

            // A List, whose own Contains the LINQ provider translates; an array's would bind to a span.
            var ids = expired.ToList();
            session.DeleteWhere<OidcUsedNonce>(n => ids.Contains(n.Id));
            await session.SaveChangesAsync(ct);
            removed += expired.Count;
            if (expired.Count < PurgeBatchSize)
            {
                return removed;
            }
        }

        logger.LogWarning(
            "Purged {Removed} expired OIDC grant nonces and stopped at the batch limit; the next purge takes the rest",
            removed);
        return removed;
    }
}
