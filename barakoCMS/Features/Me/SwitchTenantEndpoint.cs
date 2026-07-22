using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Multitenancy;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace barakoCMS.Features.Me;

public class SwitchTenantRequest
{
    /// <summary>The handle of the club to switch into.</summary>
    public string Club { get; set; } = string.Empty;
}

public class SwitchTenantResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }
}

/// <summary>
/// POST /api/me/switch — the signed-in user swaps their token for one scoped to another club they
/// belong to, without re-authenticating. The server verifies an active membership (so this can only
/// ever mint a token for a club the caller is already entitled to) and bakes that club's roles into
/// the new token. Device binding (the <c>did</c> claim) is carried over so device-trust still holds.
/// </summary>
public class SwitchTenantEndpoint : Endpoint<SwitchTenantRequest, SwitchTenantResponse>
{
    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public SwitchTenantEndpoint(IDocumentSession session, IConfiguration config, barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer)
    {
        _session = session;
        _config = config;
        _tokenIssuer = tokenIssuer;
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
            await SendUnauthorizedAsync(ct);
            return;
        }

        // Carry the device binding forward so DeviceTrust enforcement keeps working after a switch.
        var did = User.FindFirst("did")?.Value;
        var device = DeviceContext.From(HttpContext);
        var extraClaims = new List<Claim>();
        if (!string.IsNullOrEmpty(did))
            extraClaims.Add(new("did", did));

        // This endpoint already performed the membership check correctly and was the model for
        // ITokenIssuer; it now delegates so there is exactly one implementation to keep right.
        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, target, extraClaims, ct);
        if (!issued.Allowed)
        {
            ThrowError(r => r.Club, "You are not a member of this club.");
            return;
        }

        var accessTokenExpiry = issued.ExpiresAt;
        var jwtToken = issued.Token;

        var refreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        _session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshTokenString,
            UserId = user.Id,
            ExpiresAt = refreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });
        await _session.SaveChangesAsync(ct);

        await SendAsync(new SwitchTenantResponse
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry,
        });
    }
}
