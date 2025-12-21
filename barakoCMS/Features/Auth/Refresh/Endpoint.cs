using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Refresh;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _querySession;
    private readonly IDocumentSession _documentSession;
    private readonly IConfiguration _config;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IQuerySession querySession,
        IDocumentSession documentSession,
        IConfiguration config,
        ILogger<Endpoint> logger)
    {
        _querySession = querySession;
        _documentSession = documentSession;
        _config = config;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/auth/refresh");
        AllowAnonymous(); // No auth required, validated by refresh token
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Find the refresh token
        var refreshToken = await _querySession.Query<RefreshToken>()
            .FirstOrDefaultAsync(rt => rt.Token == req.RefreshToken, ct);

        if (refreshToken == null)
        {
            _logger.LogWarning("Refresh attempt with invalid token");
            ThrowError("Invalid refresh token");
            return;
        }

        // Check if token is expired
        if (refreshToken.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Refresh attempt with expired token. UserId: {UserId}, Expired: {ExpiresAt}",
                refreshToken.UserId, refreshToken.ExpiresAt);
            ThrowError("Refresh token has expired. Please log in again.");
            return;
        }

        // Check if token is revoked
        if (refreshToken.IsRevoked)
        {
            _logger.LogWarning(
                "Refresh attempt with revoked token. UserId: {UserId}, Reason: {Reason}",
                refreshToken.UserId, refreshToken.RevokedReason);
            ThrowError("Refresh token has been revoked. Please log in again.");
            return;
        }

        // Load the user
        var user = await _querySession.LoadAsync<User>(refreshToken.UserId, ct);
        if (user == null)
        {
            _logger.LogError("User not found for valid refresh token. UserId: {UserId}", refreshToken.UserId);
            ThrowError("User not found");
            return;
        }

        // Load user roles
        var roles = await _querySession.Query<Role>()
            .Where(r => user.RoleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        // Generate new access token (15 minutes)
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
                foreach (var role in roles)
                {
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, role));
                }
                if (!roles.Any())
                {
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, "User"));
                }
            });

        // Generate new refresh token (rotation)
        var newRefreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var newRefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        
        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = newRefreshTokenString,
            UserId = user.Id,
            ExpiresAt = newRefreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        };

        // Revoke old refresh token
        refreshToken.IsRevoked = true;
        refreshToken.RevokedReason = "used";
        refreshToken.RevokedAt = DateTime.UtcNow;
        
        _documentSession.Update(refreshToken);
        _documentSession.Store(newRefreshToken);
        await _documentSession.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Token refreshed for user: {Username}, UserId: {UserId}",
            user.Username, user.Id);

        await SendAsync(new Response
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = newRefreshTokenString,
            RefreshTokenExpiry = newRefreshTokenExpiry
        });
    }
}
