using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using Marten.Patching;
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
    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Core.Interfaces.IOtpService _otp;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public Endpoint(
        barakoCMS.Repository.IUserRepository repo,
        IQuerySession session,
        IDocumentSession documentSession,
        IConfiguration _config,
        ILogger<Endpoint> logger,
        barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
        barakoCMS.Core.Interfaces.IOtpService otp,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer)
    {
        _repo = repo;
        _session = session;
        _documentSession = documentSession;
        this._config = _config;
        _logger = logger;
        _deviceGate = deviceGate;
        _otp = otp;
        _tenant = tenant;
        _tokenIssuer = tokenIssuer;
    }

    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth")); // 5 attempts per 15 minutes
    }

    // Dummy password hash for timing attack prevention (pre-computed BCrypt hash)
    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy_password_for_timing_attack_prevention");

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _repo.GetByUsernameAsync(req.Username, ct);

        if (user == null)
        {
            // Prevent timing attack: always perform BCrypt verification even for non-existent users
            // This ensures consistent response time regardless of whether user exists
            BCrypt.Net.BCrypt.Verify(req.Password, DummyPasswordHash);

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
            // Atomic SQL-level increment so concurrent failed attempts can't be lost to a
            // read-modify-write race (which would let an attacker bypass the lockout threshold).
            _documentSession.Patch<User>(user.Id).Increment(x => x.FailedLoginAttempts);
            await _documentSession.SaveChangesAsync(ct);

            // Re-read the authoritative count and lock out once the threshold is reached.
            var refreshed = await _session.LoadAsync<User>(user.Id, ct);
            var attempts = refreshed?.FailedLoginAttempts ?? 0;

            if (attempts >= 5)
            {
                _documentSession.Patch<User>(user.Id).Set(x => x.LockoutUntil, DateTime.UtcNow.AddMinutes(15));
                await _documentSession.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "Account locked due to failed login attempts: {Username}",
                    req.Username);
            }

            _logger.LogWarning(
                "Failed login attempt for user: {Username}, Attempts: {Attempts}",
                req.Username, attempts);

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

        // Device trust: if this password sign-in comes from an unknown device, don't issue tokens —
        // send an OTP so the user can approve the device (which trusts it on verify).
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        var gate = await _deviceGate.EvaluatePasswordAsync(user, device, ct);
        if (gate.Decision == barakoCMS.Core.Interfaces.DeviceDecision.ApprovalRequired)
        {
            await _otp.SendCodeAsync(user.Email, device, ct);
            _logger.LogInformation("Password login from an unapproved device for {Username}; sent approval OTP", user.Username);
            await SendAsync(new Response
            {
                RequiresDeviceApproval = true,
                Message = "This device isn't approved yet. Enter the code we emailed to approve it.",
                Email = user.Email,
            });
            return;
        }

        // Mint through the issuer so the tenant-access check runs. `X-Tenant` is client-supplied, so
        // valid credentials alone must not be enough to get a token scoped to an arbitrary tenant.
        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, _tenant.Slug, gate.Claims, ct);
        if (!issued.Allowed)
        {
            // Same message as bad credentials on purpose: telling an attacker "right password,
            // wrong tenant" confirms both the account and the tenant's existence.
            _logger.LogWarning(
                "Login refused for {Username}: not permitted on tenant {Tenant} ({Reason})",
                user.Username, _tenant.Slug, issued.DenialReason);
            ThrowError("Invalid credentials");
            return;
        }

        var jti = issued.Jti;
        var accessTokenExpiry = issued.ExpiresAt;
        var jwtToken = issued.Token;

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
            IsRevoked = false,
            DeviceId = device.DeviceId
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
