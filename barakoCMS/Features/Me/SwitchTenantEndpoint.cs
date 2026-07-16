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

    public SwitchTenantEndpoint(IDocumentSession session, IConfiguration config)
    {
        _session = session;
        _config = config;
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

        var isDefault = target == Tenant.DefaultSlug;
        if (!isDefault)
        {
            var tenant = await _session.Query<Tenant>()
                .FirstOrDefaultAsync(t => t.Slug == target && t.IsActive, ct);
            if (tenant is null)
            {
                await SendNotFoundAsync(ct);
                return;
            }

            var isMember = await _session.Query<Membership>()
                .AnyAsync(m => m.UserId == userId && m.TenantSlug == target
                               && m.Status == MembershipStatus.Active, ct);
            if (!isMember)
            {
                ThrowError(r => r.Club, "You are not a member of this club.");
                return;
            }
        }

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(_session, user, target, ct);
        var roles = await _session.Query<Role>()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        // Carry the device binding forward so DeviceTrust enforcement keeps working after a switch.
        var did = User.FindFirst("did")?.Value;
        var device = DeviceContext.From(HttpContext);

        var jti = Guid.NewGuid().ToString();
        var accessTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        var jwtToken = JWTBearer.CreateToken(
            signingKey: _config["JWT:Key"]!,
            expireAt: accessTokenExpiry,
            issuer: _config["JWT:Issuer"],
            audience: _config["JWT:Audience"],
            privileges: u =>
            {
                u.Claims.Add(new(JwtRegisteredClaimNames.Jti, jti));
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
                u.Claims.Add(new("tenant", target));
                foreach (var role in roles)
                    u.Claims.Add(new(ClaimTypes.Role, role));
                if (roles.Count == 0)
                    u.Claims.Add(new(ClaimTypes.Role, "User"));
                if (!string.IsNullOrEmpty(did))
                    u.Claims.Add(new("did", did));
            });

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
