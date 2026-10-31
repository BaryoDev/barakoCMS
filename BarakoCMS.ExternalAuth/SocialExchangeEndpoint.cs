using System.Security.Cryptography;
using System.Text;
using FastEndpoints;
using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace BarakoCMS.ExternalAuth;

internal sealed class SocialExchangeRequest
{
    public string Code { get; set; } = string.Empty;
}

internal sealed class SocialExchangeResponse
{
    public string? Token { get; set; }

    public string? RefreshToken { get; set; }

    public bool RequiresMfa { get; set; }

    /// <summary>Finish at <c>/api/auth/mfa/verify</c> when <see cref="RequiresMfa"/> is true.</summary>
    public string? MfaChallengeToken { get; set; }

    public string Club { get; set; } = string.Empty;
}

internal sealed class SocialExchangeValidator : Validator<SocialExchangeRequest>
{
    public SocialExchangeValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(SocialHandoff.MaxCodeLength);
    }
}

/// <summary>
/// POST /api/auth/social/exchange. Turns the code a callback redirected with into the token pair or
/// the MFA challenge. Works once, within 60 seconds, from the browser the sign-in started in.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class SocialExchangeEndpoint(IDocumentSession session) : Endpoint<SocialExchangeRequest, SocialExchangeResponse>
{
    private const string Refused = "The sign-in code is not valid. Please sign in again.";

    public override void Configure()
    {
        Post("/api/auth/social/exchange");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(SocialRateLimit.ExchangePolicy));
    }

    public override async Task HandleAsync(SocialExchangeRequest req, CancellationToken ct)
    {
        // Every refusal is the same 400, so a caller learns nothing about which check failed.
        var device = HttpContext.Request.Cookies[SocialHandoff.DeviceCookie];
        if (string.IsNullOrEmpty(device) || device.Length > SocialHandoff.MaxCodeLength)
        {
            ThrowError(Refused);
            return;
        }

        var stored = await session.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(req.Code), ct);
        if (stored is null || stored.Used || stored.ExpiresAt <= DateTime.UtcNow
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(stored.DeviceHash), Encoding.ASCII.GetBytes(SocialHandoff.HashOf(device))))
        {
            ThrowError(Refused);
            return;
        }

        var outcome = SocialHandoff.Open(req.Code, stored.Payload);
        if (outcome is null)
        {
            ThrowError(Refused);
            return;
        }

        stored.Used = true;
        stored.Payload = string.Empty;
        session.Store(stored);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // A concurrent exchange spent the code first, so no tokens may be returned here.
            ThrowError(Refused);
            return;
        }

        HttpContext.Response.Cookies.Delete(SocialHandoff.DeviceCookie, SocialHandoff.CookieForRemoval());
        await Send.OkAsync(new SocialExchangeResponse
        {
            Token = outcome.RequiresMfa ? null : outcome.Token,
            RefreshToken = outcome.RequiresMfa ? null : outcome.Refresh,
            RequiresMfa = outcome.RequiresMfa,
            MfaChallengeToken = outcome.MfaChallenge,
            Club = outcome.Club,
        }, ct);
    }
}
