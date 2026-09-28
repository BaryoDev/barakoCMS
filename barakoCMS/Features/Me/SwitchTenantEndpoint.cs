using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Multitenancy;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace barakoCMS.Features.Me;

internal class SwitchTenantRequest
{
    /// <summary>The handle of the club to switch into.</summary>
    public string Club { get; set; } = string.Empty;
}

internal class SwitchTenantResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    /// <summary>Always empty. Kept so the response keeps its shape.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Always the default value. Kept so the response keeps its shape.</summary>
    public DateTime RefreshTokenExpiry { get; set; }
}

/// <summary>
/// POST /api/me/switch: the signed-in user exchanges their access token for one scoped to another
/// club, without re-authenticating. The issuer checks for an active membership in the target club
/// (or treats it as unmanaged, as every token path does) and bakes that club's roles into the new
/// token. Device binding (the <c>did</c> claim) is carried over so device-trust still holds.
/// </summary>
/// <remarks>
/// It exchanges, it does not renew. The new token expires when the presented one would have, and the
/// presented one is revoked, so a chain of switches cannot keep a bearer alive past its original
/// expiry. It returns no refresh token: the one the caller already holds is not tied to a club, since
/// a refresh mints for the <c>X-Tenant</c> it is sent and re-checks membership.
/// </remarks>
internal class SwitchTenantEndpoint : Endpoint<SwitchTenantRequest, SwitchTenantResponse>
{
    private readonly IDocumentSession _session;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;
    private readonly barakoCMS.Infrastructure.Services.ITokenRevocationService _revocation;

    public SwitchTenantEndpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Services.ITokenRevocationService revocation)
    {
        _session = session;
        _tokenIssuer = tokenIssuer;
        _revocation = revocation;
    }

    public override void Configure()
    {
        Post("/api/me/switch"); // authenticated by default
    }

    public override async Task HandleAsync(SwitchTenantRequest req, CancellationToken ct)
    {
        var target = (req.Club ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(target))
        {
            ThrowError(r => r.Club, "A club is required.");
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var user = await _session.Query<User>().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Carry the device binding forward so DeviceTrust enforcement keeps working after a switch.
        var did = User.FindFirst("did")?.Value;
        var extraClaims = new List<Claim>();
        if (!string.IsNullOrEmpty(did))
            extraClaims.Add(new("did", did));

        // This endpoint already performed the membership check correctly and was the model for
        // ITokenIssuer; it now delegates so there is exactly one implementation to keep right.
        // The bearer middleware refuses a token without exp, so it is always here.
        if (!long.TryParse(User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out var expUnix))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var presentedExpiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;

        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, target, extraClaims, presentedExpiry, ct);
        if (!issued.Allowed)
        {
            ThrowError(r => r.Club, "You are not a member of this club.");
            return;
        }

        var presentedJti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (!string.IsNullOrEmpty(presentedJti))
            await _revocation.RevokeTokenAsync(presentedJti, user.Id, "switched", presentedExpiry, ct);

        await Send.ResponseAsync(new SwitchTenantResponse
        {
            Token = issued.Token,
            Expiry = issued.ExpiresAt,
        });
    }
}
