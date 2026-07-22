using System.Security.Cryptography;
using FastEndpoints.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// Shared sign-in for external identity providers (Facebook, LinkedIn, …). A provider proves the
/// person's email; we match it to a global user (creating one if new) and mint the same tenant-scoped,
/// device-bound access + refresh token the email/password flows issue. One place so every provider
/// stays consistent with core's auth rules — the seam that will move into a barakoCMS module.
/// </summary>
public static class SocialSignIn
{
    /// <summary>
    /// Tokens for a successful sign-in, or <see cref="Denied"/> when the person proved their email
    /// but has no claim to the club they asked for.
    /// </summary>
    public sealed record Tokens(string Token, string Refresh)
    {
        public bool Allowed => Token.Length > 0;

        public static Tokens Denied() => new(string.Empty, string.Empty);
    }

    /// <summary>Profile fields a provider shared (any may be null). Only non-null values are stored.</summary>
    public sealed record ProfileData(
        string? Name, string? PhotoUrl, string? Birthday, string? Location, string Source);

    public static async Task<Tokens> IssueAsync(
        IDocumentSession session,
        IConfiguration config,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        HttpContext http,
        string email,
        string club,
        CancellationToken ct,
        ProfileData? profile = null)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await session.Query<User>().FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
        if (user is null)
        {
            user = new User { Id = Guid.NewGuid(), Email = normalized, Username = normalized, PasswordHash = "" };
            session.Store(user);
        }

        if (profile is not null)
        {
            var sp = await session.Query<SocialProfile>().FirstOrDefaultAsync(p => p.UserId == user.Id, ct)
                     ?? new SocialProfile { Id = Guid.NewGuid(), UserId = user.Id };
            // Merge: keep existing values when this provider didn't share a field.
            sp.Name = profile.Name ?? sp.Name;
            sp.PhotoUrl = profile.PhotoUrl ?? sp.PhotoUrl;
            sp.Birthday = profile.Birthday ?? sp.Birthday;
            sp.Location = profile.Location ?? sp.Location;
            sp.Source = profile.Source;
            sp.UpdatedAt = DateTime.UtcNow;
            session.Store(sp);
        }

        var tenantSlug = string.IsNullOrEmpty(club) ? Tenant.DefaultSlug : club;

        var device = DeviceContext.From(http);
        var deviceClaims = await deviceGate.TrustOnOtpAsync(user, device, ct);

        // A provider proving the person owns an email address says who they are, not which clubs
        // they belong to — `club` arrives from the caller exactly like the X-Tenant header does.
        // Mint through core's issuer so this module is held to the same membership check as the
        // password and OTP flows rather than re-deciding it here.
        var issued = await tokenIssuer.IssueAccessTokenAsync(user, tenantSlug, deviceClaims, ct);
        if (!issued.Allowed)
        {
            // The user record and any profile merge above are still worth persisting; they are not
            // sensitive and re-doing the provider round trip on a retry would be wasteful.
            await session.SaveChangesAsync(ct);
            return Tokens.Denied();
        }

        var jwt = issued.Token;

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refresh,
            UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });
        await session.SaveChangesAsync(ct);

        return new Tokens(jwt, refresh);
    }

    /// <summary>The SPA callback URL the provider callbacks redirect to, with tokens in the fragment.</summary>
    public static string FrontendCallback(string baseUrl, string token, string refresh, string club) =>
        $"{baseUrl}/auth/social#token={Uri.EscapeDataString(token)}" +
        $"&refresh={Uri.EscapeDataString(refresh)}&club={Uri.EscapeDataString(club)}";
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
