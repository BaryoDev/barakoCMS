using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Login;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly barakoCMS.Repository.IUserRepository _repo;
    private readonly IQuerySession _session;
    private readonly IDocumentSession _documentSession;
    private readonly IConfiguration _config;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        barakoCMS.Repository.IUserRepository repo,
        IQuerySession session,
        IDocumentSession documentSession,
        IConfiguration _config,
        ILogger<Endpoint> logger)
    {
        _repo = repo;
        _session = session;
        _documentSession = documentSession;
        this._config = _config;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth")); // 5 attempts per 15 minutes
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _repo.GetByUsernameAsync(req.Username, ct);

        if (user == null)
        {
            _logger.LogWarning("Login attempt for non-existent user: {Username}", req.Username);
            ThrowError("Invalid credentials");
            return;
        }

        // Check if account is locked out
        if (user.LockoutUntil.HasValue && user.LockoutUntil.Value > DateTime.UtcNow)
        {
            var remainingMinutes = (int)(user.LockoutUntil.Value - DateTime.UtcNow).TotalMinutes + 1;
            _logger.LogWarning(
                "Login attempt for locked account: {Username}, Lockout until: {LockoutUntil}",
                req.Username, user.LockoutUntil.Value);
            
            ThrowError($"Account is locked due to multiple failed login attempts. Please try again in {remainingMinutes} minute(s).");
            return;
        }

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            // Increment failed login attempts
            user.FailedLoginAttempts++;
            
            if (user.FailedLoginAttempts >= 5)
            {
                // Lock account for 15 minutes
                user.LockoutUntil = DateTime.UtcNow.AddMinutes(15);
                _logger.LogWarning(
                    "Account locked due to 5 failed login attempts: {Username}",
                    req.Username);
            }
            
            _documentSession.Update(user);
            await _documentSession.SaveChangesAsync(ct);
            
            _logger.LogWarning(
                "Failed login attempt for user: {Username}, Attempts: {Attempts}",
                req.Username, user.FailedLoginAttempts);
            
            ThrowError("Invalid credentials");
            return;
        }

        // Successful login - reset failed attempts
        if (user.FailedLoginAttempts > 0 || user.LockoutUntil.HasValue)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutUntil = null;
            _documentSession.Update(user);
            await _documentSession.SaveChangesAsync(ct);
        }

        // Load user roles
        var roles = await _session.Query<Role>()
            .Where(r => user.RoleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        // Generate JWT with 15-minute expiry
        var jti = Guid.NewGuid().ToString(); // JWT ID for revocation tracking
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
                // Fallback for backward compatibility or default
                if (!roles.Any())
                {
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, "User"));
                }
            });

        // Generate refresh token (7-day expiry)
        var refreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshTokenString,
            UserId = user.Id,
            ExpiresAt = refreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false
        };

        _documentSession.Store(refreshToken);
        await _documentSession.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Successful login for user: {Username}, UserId: {UserId}",
            user.Username, user.Id);

        await SendAsync(new Response
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry
        });
    }
}
