using System.Security.Cryptography;
using FastEndpoints.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// Shared sign-in for external identity providers (Facebook, LinkedIn, and the rest). A provider
/// proves the person's email; we match it to a global user (creating one if new) and mint the same tenant-scoped,
/// device-bound access + refresh token the email/password flows issue. One place so every provider
/// stays consistent with core's auth rules — the seam that will move into a barakoCMS module.
/// </summary>
public static class SocialSignIn
{
    /// <summary>
    /// Tokens for a successful sign-in, or <see cref="Denied"/> when the provider did not verify the
    /// email, or the person proved their email but has no claim to the club they asked for.
    /// </summary>
    public sealed record Tokens(string Token, string Refresh, bool RequiresMfa = false, string? MfaChallenge = null)
    {
        public bool Allowed => Token.Length > 0;

        public static Tokens Denied() => new(string.Empty, string.Empty);

        /// <summary>Email proven, but the account has MFA enrolled — finish at /api/auth/mfa/verify.</summary>
        public static Tokens MfaRequired(string challenge) => new(string.Empty, string.Empty, true, challenge);
    }

    /// <summary>Profile fields a provider shared (any may be null). Only non-null values are stored.</summary>
    public sealed record ProfileData(
        string? Name, string? PhotoUrl, string? Birthday, string? Location, string Source);

    /// <summary>
    /// Sign-in through one of the OAuth providers (Google, Facebook, GitHub, LinkedIn). It names the
    /// account by the provider's own user id, so a person whose address changes at the provider
    /// still reaches the same local user. Needs <paramref name="provider"/> to be one of the
    /// <see cref="ExternalProviders"/> keys, which is also the issuer of the link.
    /// </summary>
    /// <remarks>
    /// <c>subject</c> is the provider's stable id for the person: Google and LinkedIn <c>sub</c>,
    /// Facebook <c>id</c>, GitHub numeric id. <c>emailVerified</c> is the provider's own rule, and
    /// only a first sign-in reads it, to match a local user by address. A linked account is found by
    /// subject, with or without an address. <c>claims</c> is what the provider says that the person
    /// cannot edit, for the tenant's role map. <c>emailTrustedByOperator</c> marks a verification the
    /// provider did not make: it still lets an unlinked sign-in match by address, but links nothing
    /// to the user it matched, so the match does not outlive the operator's setting.
    /// </remarks>
    public static Task<(Tokens Tokens, bool EmailNotVerified)> IssueForProviderAsync(
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa,
        HttpContext http,
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        string club,
        CancellationToken ct,
        ProfileData? profile = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? claims = null,
        bool emailTrustedByOperator = false)
    {
        // A linked account signs in without an address. An unlinked one needs a usable address, and
        // without one the sign-in is refused the same way an unverified address is.
        var usable = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(usable) || !usable.Contains('@'))
        {
            usable = null;
        }

        var identity = new OidcIdentity(provider, subject, usable, emailVerified, profile?.Name, profile?.PhotoUrl)
        {
            Claims = claims ?? new Dictionary<string, IReadOnlyList<string>>(),
            EmailTrustedByOperator = emailTrustedByOperator,
        };
        return IssueForIdentityAsync(
            session, config, deviceGate, tokenIssuer, mfa, http, identity, provider, club, ct, null, profile);
    }

    /// <summary>
    /// Sign-in for a provider that names the account by issuer and subject. A known pair signs its
    /// linked user in whatever the token says about email. An unknown pair is linked by email, and
    /// only when the provider vouches for the address: to the local user holding it, or to a new
    /// one. Without that the sign-in is refused and nothing is stored.
    /// </summary>
    /// <returns>
    /// The tokens, and whether the refusal was the unverified address rather than the tenant check.
    /// </returns>
    internal static async Task<(Tokens Tokens, bool EmailNotVerified)> IssueForIdentityAsync(
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa,
        HttpContext http,
        OidcIdentity identity,
        string provider,
        string club,
        CancellationToken ct,
        Action<IDocumentSession>? beforeFinalCommit = null,
        ProfileData? profileOverride = null)
    {
        if (!await TenantSignIn.AllowsAsync(session, club, TenantSignInPolicy.Social, ct))
        {
            return (Tokens.Denied(), false);
        }

        var key = ExternalIdentity.KeyOf(identity.Issuer, identity.Subject);
        var link = await session.LoadAsync<ExternalIdentity>(key, ct);

        // A link whose user has been deleted is treated as no link. Otherwise removing an account
        // would lock its provider identity out for good, with nothing an administrator could undo.
        var user = link is null ? null : await session.LoadAsync<User>(link.UserId, ct);
        var signUp = await SignUpPolicy.DecideAsync(session, club, ct);
        var created = false;

        if (user is null)
        {
            var email = identity.Email;
            if (email is null || !identity.EmailVerified)
            {
                return (Tokens.Denied(), true);
            }

            user = await session.Query<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == email, ct);
            if (user is null)
            {
                if (signUp.Outcome == SignUpOutcome.Refused)
                {
                    return (Tokens.Denied(), false);
                }

                // NormalizedUsername is unique, and somebody may already use this address as a name.
                var username = await session.Query<User>().AnyAsync(u => u.NormalizedUsername == email, ct)
                    ? $"{email}+{Guid.NewGuid():N}"[..(email.Length + 9)]
                    : email;
                user = new User { Id = Guid.NewGuid(), Email = email, Username = username, PasswordHash = "" };
                session.Store(user);
                created = true;
            }

            // An address the operator chose to trust, and the provider did not vouch for, may sign
            // in to the account holding it, but is not made permanent. A new account is its own.
            if (created || !identity.EmailTrustedByOperator)
            {
                session.Store(new ExternalIdentity
                {
                    Id = key,
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    UserId = user.Id,
                    Provider = provider,
                    LinkedAt = DateTime.UtcNow,
                });
            }
        }

        var mapped = await MappedRolesAsync(session, config, identity, provider, club, ct);

        var profile = profileOverride ?? new ProfileData(identity.Name, identity.Picture, null, null, provider);
        return (await IssueForUserAsync(
            session, config, deviceGate, tokenIssuer, mfa, http, user, club, created ? signUp : null, ct, profile, beforeFinalCommit, mapped), false);
    }

    /// <summary>
    /// Everything after the provider has said who this is: the profile merge, the second factor, the
    /// device, and the token from core's issuer, which is where tenant membership is decided.
    /// </summary>
    /// <param name="session">The request's session, which this commits.</param>
    /// <param name="config">Where the MFA challenge is signed from.</param>
    /// <param name="deviceGate">Trusts the device the sign-in came from.</param>
    /// <param name="tokenIssuer">Core's issuer, which decides tenant membership.</param>
    /// <param name="mfa">Whether the account has a second factor.</param>
    /// <param name="http">The request, for the device.</param>
    /// <param name="user">Who the provider proved this is.</param>
    /// <param name="club">The tenant asked for.</param>
    /// <param name="signUp">
    /// The tenant's sign-up decision when this request created the account, which an Open tenant
    /// acts on here. Null for an existing account: that is a sign-in, the same as registering with an
    /// address that already has an account, and the issuer's membership check decides it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="profile">What the provider shared, merged into the stored profile.</param>
    /// <param name="beforeFinalCommit">
    /// Queues writes that must commit with the outcome and with nothing earlier. Trusting a device
    /// commits the session on its own, so work queued before that would commit before the token is
    /// issued.
    /// </param>
    private static async Task<Tokens> IssueForUserAsync(
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa,
        HttpContext http,
        User user,
        string club,
        SignUpDecision? signUp,
        CancellationToken ct,
        ProfileData? profile,
        Action<IDocumentSession>? beforeFinalCommit,
        MappedRoles? mapped)
    {
        // Open sign-up makes this person a member before the issuer looks for one. Committed now,
        // with the account it may have just created, because the issuer reads memberships from
        // the database and nothing queued by beforeFinalCommit is in the session yet.
        if (signUp is not null && await SignUpPolicy.StageJoinAsync(session, signUp, user, ct))
        {
            await session.SaveChangesAsync(ct);
        }

        if (profile is not null)
        {
            await StageProfileAsync(session, user, profile, ct);
        }

        var tenantSlug = string.IsNullOrEmpty(club) ? Tenant.DefaultSlug : club;

        // A provider proving email ownership is a first factor. If the account enrolled MFA, do not mint
        // tokens here — return a challenge so the flow completes at /api/auth/mfa/verify. Otherwise a
        // provider account takeover would sidestep the second factor.
        if (await mfa.IsEnabledAsync(user.Id, ct))
        {
            beforeFinalCommit?.Invoke(session);
            await session.SaveChangesAsync(ct); // persist the user/profile created above
            var challenge = await MfaChallengeAsync(session, config, user, tenantSlug, mapped, ct);
            return Tokens.MfaRequired(challenge);
        }

        var device = DeviceContext.From(http);
        var deviceClaims = await deviceGate.TrustOnOtpAsync(user, device, ct);
        if (mapped is not null)
        {
            return await IssueWithGrantAsync(
                session, http, user, tenantSlug, mapped, deviceClaims, device, beforeFinalCommit, ct);
        }

        // A provider proving the person owns an email address says who they are, not which clubs
        // they belong to — `club` arrives from the caller exactly like the X-Tenant header does.
        // Mint through core's issuer so this module is held to the same membership check as the
        // password and OTP flows rather than re-deciding it here.
        var issued = await tokenIssuer.IssueAccessTokenAsync(user, tenantSlug, deviceClaims, ct);
        if (!issued.Allowed)
        {
            // The user record and any profile merge above are still worth persisting; they are not
            // sensitive and re-doing the provider round trip on a retry would be wasteful.
            beforeFinalCommit?.Invoke(session);
            await session.SaveChangesAsync(ct);
            return Tokens.Denied();
        }

        var jwt = issued.Token;

        var refresh = StageRefreshToken(session, user, issued.RefreshLifetime, device.DeviceId);
        beforeFinalCommit?.Invoke(session);
        await session.SaveChangesAsync(ct);

        return new Tokens(jwt, refresh);
    }

    /// <summary>
    /// The challenge for a person who owes a second factor. It carries the roles the map earned,
    /// minus any a claim may not grant, for core's verify step to grant once the factor passes.
    /// </summary>
    private static async Task<string> MfaChallengeAsync(
        IDocumentSession session, IConfiguration config, User user, string tenantSlug, MappedRoles? mapped,
        CancellationToken ct)
    {
        barakoCMS.Infrastructure.Auth.Mfa.PendingExternalGrant? grant = null;
        if (mapped is not null && tenantSlug != Tenant.DefaultSlug)
        {
            var refused = await barakoCMS.Infrastructure.Auth.ExternalRoleGrant.RefusedAsync(session, tenantSlug, mapped.RoleIds, ct);
            var roles = mapped.RoleIds.Except(refused).ToList();
            grant = roles.Count == 0 ? null : new(tenantSlug, mapped.Provider, roles);
        }

        return barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Create(config, user.Id, grant).Token;
    }

    private static string StageRefreshToken(IDocumentSession session, User user, TimeSpan lifetime, string? deviceId)
    {
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = RefreshToken.HashOf(refresh),
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.Add(lifetime),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = deviceId,
        });
        return refresh;
    }

    /// <summary>
    /// Grants the mapped roles, issues the token and stores the refresh token in one transaction,
    /// so a sign-in the issuer refuses grants nothing. The issuer reads memberships through that
    /// transaction, so it sees the grant before it commits. Runs after the second factor: a person
    /// with one enrolled is returned a challenge before this, and core's verify step grants what the
    /// challenge carries.
    /// </summary>
    /// <remarks>
    /// The account, link and profile commit first, as they do when the issuer refuses.
    /// <paramref name="beforeFinalCommit"/> joins the transaction, so the nonce is spent with the
    /// grant and the token, or with the refusal.
    /// </remarks>
    private static async Task<Tokens> IssueWithGrantAsync(
        IDocumentSession session, HttpContext http, User user, string tenantSlug, MappedRoles mapped,
        IReadOnlyList<Claim> deviceClaims, DeviceContext device, Action<IDocumentSession>? beforeFinalCommit,
        CancellationToken ct)
    {
        await session.SaveChangesAsync(ct);

        var granted = false;
        var transaction = http.RequestServices.GetRequiredService<barakoCMS.Infrastructure.Services.IContentBatchRunner>();
        var tokens = await transaction.RunAsync<Tokens?>(async (scope, token) =>
        {
            var writes = scope.GetRequiredService<IDocumentSession>();
            granted = await StageMappedRolesAsync(writes, user, tenantSlug, mapped, token);
            await writes.SaveChangesAsync(token);

            var issued = await scope.GetRequiredService<ITokenIssuer>()
                .IssueAccessTokenAsync(user, tenantSlug, deviceClaims, token);
            if (!issued.Allowed)
            {
                return new barakoCMS.Infrastructure.Services.ContentBatchOutcome<Tokens?>(null, Commit: false);
            }

            var refresh = StageRefreshToken(writes, user, issued.RefreshLifetime, device.DeviceId);
            beforeFinalCommit?.Invoke(writes);
            return new barakoCMS.Infrastructure.Services.ContentBatchOutcome<Tokens?>(
                new Tokens(issued.Token, refresh), Commit: true);
        }, ct);

        if (tokens is null)
        {
            beforeFinalCommit?.Invoke(session);
            await session.SaveChangesAsync(ct);
            return Tokens.Denied();
        }

        if (granted)
        {
            http.RequestServices.GetService<barakoCMS.Infrastructure.Services.IPermissionResolver>()
                ?.InvalidateUserPermissions(user.Id);
        }

        return tokens;
    }

    /// <summary>The roles the tenant's map gives for a sign-in's claims, and the provider they came from.</summary>
    internal sealed record MappedRoles(List<Guid> RoleIds, string Provider);

    /// <summary>
    /// Reads the tenant's map and what it gives this sign-in. Nothing is written, so a person who
    /// still owes a second factor has been given nothing yet. The map runs on every sign-in, so a
    /// person added to a group at the provider has the role the next time they sign in.
    /// </summary>
    private static async Task<MappedRoles?> MappedRolesAsync(
        IDocumentSession session, IConfiguration config, OidcIdentity identity, string provider, string club, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(club) || identity.Claims.Count == 0)
        {
            return null;
        }

        var map = await session.LoadAsync<ExternalRoleMap>(club.Trim().ToLowerInvariant(), ct);
        if (map is null)
        {
            return null;
        }

        var roles = RoleMaps.RolesFor(map, identity.Claims, provider, identity.Issuer, RoleMaps.IssuerRequired(config, provider));
        return roles.Count == 0 ? null : new MappedRoles(roles, provider);
    }

    private static Task<bool> StageMappedRolesAsync(
        IDocumentSession session, User user, string tenantSlug, MappedRoles mapped, CancellationToken ct) =>
        ExternalRoleGrant.StageAsync(session, user, tenantSlug, mapped.RoleIds, mapped.Provider, ct);

    /// <summary>Merges what the provider shared into the stored profile, keeping a field it left out.</summary>
    private static async Task StageProfileAsync(IDocumentSession session, User user, ProfileData profile, CancellationToken ct)
    {
        var sp = await session.Query<SocialProfile>().FirstOrDefaultAsync(p => p.UserId == user.Id, ct)
                 ?? new SocialProfile { Id = Guid.NewGuid(), UserId = user.Id };
        sp.Name = profile.Name ?? sp.Name;
        sp.PhotoUrl = profile.PhotoUrl ?? sp.PhotoUrl;
        sp.Birthday = profile.Birthday ?? sp.Birthday;
        sp.Location = profile.Location ?? sp.Location;
        sp.Source = profile.Source;
        sp.UpdatedAt = DateTime.UtcNow;
        session.Store(sp);
    }

    /// <summary>The SPA callback URL the provider callbacks redirect to, with tokens in the fragment.</summary>
    public static string FrontendCallback(string baseUrl, string token, string refresh, string club) =>
        $"{baseUrl}/auth/social#token={Uri.EscapeDataString(token)}" +
        $"&refresh={Uri.EscapeDataString(refresh)}&club={Uri.EscapeDataString(club)}";

    /// <summary>Callback when the account needs MFA: carries the challenge for the SPA to complete /mfa/verify.</summary>
    public static string FrontendMfaCallback(string baseUrl, string challenge, string club) =>
        $"{baseUrl}/auth/social#mfa_challenge={Uri.EscapeDataString(challenge)}&club={Uri.EscapeDataString(club)}";
}

/// <summary>
/// A user's profile details gathered from a social sign-in (birthday, photo, location). Global,
/// keyed to the user — kept separate from the core User so identity stays lean.
/// </summary>
public class SocialProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Name { get; set; }
    public string? PhotoUrl { get; set; }

    /// <summary>As provided by the provider (e.g. Facebook returns MM/DD/YYYY).</summary>
    public string? Birthday { get; set; }
    public string? Location { get; set; }

    /// <summary>Which provider last populated this (e.g. "facebook", "linkedin").</summary>
    public string? Source { get; set; }
    public DateTime UpdatedAt { get; set; }
}
