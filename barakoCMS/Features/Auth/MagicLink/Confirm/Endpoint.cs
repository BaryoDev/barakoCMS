using System.Security.Cryptography;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Auth.MagicLink.Confirm;

/// <summary>
/// POST /api/auth/magic-link/confirm: spend a link for the same tokens the code verify route
/// issues. Single-use, expiry-checked, and only for the tenant the link was requested in.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class Endpoint(
    IDocumentSession session,
    IConfiguration config,
    IDeviceGate deviceGate,
    TenantContext tenant,
    ITokenIssuer tokenIssuer,
    IMfaService mfa) : Endpoint<MagicLinkConfirmRequest, MagicLinkConfirmResponse>
{
    private const string Refused = "Invalid or expired link.";

    public override void Configure()
    {
        Post("/api/auth/magic-link/confirm");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task OnBeforeHandleAsync(MagicLinkConfirmRequest req, CancellationToken ct)
    {
        if (!await TenantSignIn.AllowsAsync(session, tenant.Slug, TenantSignInPolicy.Otp, ct))
        {
            ThrowError(TenantSignIn.RefusedMessage(TenantSignInPolicy.Otp), 403);
        }
    }

    public override async Task HandleAsync(MagicLinkConfirmRequest req, CancellationToken ct)
    {
        var link = await FindUsableAsync(req.Token, ct);
        if (link is null)
        {
            ThrowError(Refused);
            return;
        }

        link.Consumed = true;
        session.Update(link);

        var user = await session.Query<User>()
            .Where(u => u.NormalizedEmail == link.Email)
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            await TrySaveAsync(ct);
            ThrowError(Refused);
            return;
        }

        // A mailbox is a first factor. With MFA enrolled it cannot mint tokens on its own.
        if (await mfa.IsEnabledAsync(user.Id, ct))
        {
            if (!await TrySaveAsync(ct)) { ThrowError(Refused); return; }
            var (challenge, _) = MfaChallengeToken.Create(config, user.Id);
            await Send.ResponseAsync(new MagicLinkConfirmResponse { RequiresMfa = true, MfaChallengeToken = challenge });
            return;
        }

        await IssueAsync(user, ct);
    }

    private async Task<MagicLinkToken?> FindUsableAsync(string? raw, CancellationToken ct)
    {
        if (!EmailVerificationToken.TryParse(raw, out var id, out var secret))
        {
            return null;
        }

        var link = await session.LoadAsync<MagicLinkToken>(id, ct);
        if (link is null || link.Consumed || link.ExpiresAt < DateTime.UtcNow)
        {
            return null;
        }

        var sameTenant = string.Equals(link.TenantSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase);
        return sameTenant && EmailVerificationToken.Matches(secret, link.TokenHash) ? link : null;
    }

    private async Task IssueAsync(User user, CancellationToken ct)
    {
        var device = DeviceContext.From(HttpContext);
        var deviceClaims = await deviceGate.TrustOnOtpAsync(user, device, ct);

        var issued = await tokenIssuer.IssueAccessTokenAsync(user, tenant.Slug, deviceClaims, ct);
        if (!issued.Allowed)
        {
            await TrySaveAsync(ct);
            ThrowError(Refused);
            return;
        }

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshExpiry = DateTime.UtcNow.Add(issued.RefreshLifetime);
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = RefreshToken.HashOf(refresh),
            UserId = user.Id,
            ExpiresAt = refreshExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });

        // Losing this save means a concurrent request spent the link, so no tokens may be returned.
        if (!await TrySaveAsync(ct)) { ThrowError(Refused); return; }

        RefreshTokenCookie.Set(HttpContext, refresh, refreshExpiry);

        await Send.ResponseAsync(new MagicLinkConfirmResponse
        {
            Token = issued.Token,
            Expiry = issued.ExpiresAt,
            RefreshToken = refresh,
            RefreshTokenExpiry = refreshExpiry,
        });
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            return false;
        }
    }
}
