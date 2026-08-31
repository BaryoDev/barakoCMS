using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using Marten.Patching;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Login;

internal class Endpoint : Endpoint<Request, Response>
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
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa)
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
        _mfa = mfa;
    }

    private readonly barakoCMS.Infrastructure.Auth.Mfa.IMfaService _mfa;

    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public override void Configure()
    {
        Post("/api/auth/login");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth")); // 5 attempts per 15 minutes
    }

    // Dummy password hash for timing attack prevention (pre-computed BCrypt hash)
    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("dummy_password_for_timing_attack_prevention");

    /// <summary>
    /// True when the password matches. An account with no password set never matches, and costs the
    /// same time as one that does.
    /// </summary>
    private static bool PasswordMatches(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            BCrypt.Net.BCrypt.Verify(password, DummyPasswordHash);
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, hash);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        var user = await _repo.GetByUsernameAsync(req.Username, ct);

        if (user == null)
        {
            // Prevent timing attack: always perform BCrypt verification even for non-existent users
            // This ensures consistent response time regardless of whether user exists
            BCrypt.Net.BCrypt.Verify(req.Password, DummyPasswordHash);

            _logger.LogWarning("Login attempt for non-existent user: {Username}", req.Username);
            await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.login.failed", null, req.Username,
                metadata: new() { ["reason"] = "unknown_user" }, ipAddress: device.IpAddress, ct: ct);
            await _documentSession.SaveChangesAsync(ct);
            ThrowError("Invalid credentials", 401);
            return;
        }

        // Check if account is locked out
        if (user.LockoutUntil.HasValue && user.LockoutUntil.Value > DateTime.UtcNow)
        {
            var remainingMinutes = (int)(user.LockoutUntil.Value - DateTime.UtcNow).TotalMinutes + 1;
            _logger.LogWarning(
                "Login attempt for locked account: {Username}, Lockout until: {LockoutUntil}",
                req.Username, user.LockoutUntil.Value);

            await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.login.blocked", user.Id, user.Username,
                metadata: new() { ["reason"] = "locked_out", ["lockoutUntil"] = user.LockoutUntil.Value }, ipAddress: device.IpAddress, ct: ct);
            await _documentSession.SaveChangesAsync(ct);
            ThrowError($"Account is locked due to multiple failed login attempts. Please try again in {remainingMinutes} minute(s).", 423);
            return;
        }

        // Verify password.
        //
        // An account created by social sign-in has PasswordHash = "", and BCrypt.Verify throws
        // SaltParseException on an empty hash rather than returning false. That turned a password
        // attempt against a social-created account into a 500 while every other bad-credential path
        // returns the same 401, which is a username oracle sitting on the one endpoint that took
        // care to avoid one (see the dummy-hash timing defence above). Burn the same dummy verify so
        // the timing matches too, then fail closed with the identical message.
        if (!PasswordMatches(req.Password, user.PasswordHash))
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
                await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.account.locked", user.Id, user.Username,
                    metadata: new() { ["attempts"] = attempts }, ipAddress: device.IpAddress, ct: ct);
                await _documentSession.SaveChangesAsync(ct);
            }

            _logger.LogWarning(
                "Failed login attempt for user: {Username}, Attempts: {Attempts}",
                req.Username, attempts);
            await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.login.failed", user.Id, user.Username,
                metadata: new() { ["reason"] = "bad_password", ["attempts"] = attempts }, ipAddress: device.IpAddress, ct: ct);
            await _documentSession.SaveChangesAsync(ct);

            ThrowError("Invalid credentials", 401);
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

        // MFA: if the account has a second factor enrolled, the password alone is not enough. Issue a
        // short-lived challenge bound to this user instead of tokens; the client completes the sign-in at
        // /api/auth/mfa/verify. This takes precedence over device approval — MFA is the stronger factor.
        if (await _mfa.IsEnabledAsync(user.Id, ct))
        {
            var (challenge, _) = barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Create(_config, user.Id);
            await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.mfa.challenge", user.Id, user.Username,
                ipAddress: device.IpAddress, ct: ct);
            await _documentSession.SaveChangesAsync(ct);
            _logger.LogInformation("Password OK for {Username}; MFA required", user.Username);
            await Send.ResponseAsync(new Response
            {
                RequiresMfa = true,
                MfaChallengeToken = challenge,
                Message = "Enter the code from your authenticator app.",
            });
            return;
        }

        // Device trust: if this password sign-in comes from an unknown device, don't issue tokens —
        // send an OTP so the user can approve the device (which trusts it on verify).
        var gate = await _deviceGate.EvaluatePasswordAsync(user, device, ct);
        if (gate.Decision == barakoCMS.Core.Interfaces.DeviceDecision.ApprovalRequired)
        {
            var sent = await _otp.SendCodeAsync(user.Email, device, ct);
            if (!sent)
            {
                // Safe to say so here: the password was already correct, so there is nothing left
                // to enumerate. Telling this caller to check their email would leave them waiting
                // for a message that was never sent, on the one path where that reads as being
                // locked out of their own instance.
                _logger.LogError("Could not send the device approval code to {Username}", user.Username);
                await Send.ResponseAsync(new Response
                {
                    RequiresDeviceApproval = true,
                    Message = "This device needs approval, but the code could not be emailed. Contact your administrator.",
                    Email = user.Email,
                }, 503);
                return;
            }

            _logger.LogInformation("Password login from an unapproved device for {Username}; sent approval OTP", user.Username);
            await Send.ResponseAsync(new Response
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
            await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.login.failed", user.Id, user.Username,
                metadata: new() { ["reason"] = "tenant_denied", ["denialReason"] = issued.DenialReason ?? "" }, ipAddress: device.IpAddress, ct: ct);
            await _documentSession.SaveChangesAsync(ct);
            ThrowError("Invalid credentials", 401);
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
        await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.login.succeeded", user.Id, user.Username,
            ipAddress: device.IpAddress, ct: ct);
        await _documentSession.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Successful login for user: {Username}, UserId: {UserId}",
            user.Username, user.Id);

        // Also in a cookie page script cannot read. The body still carries it for
        // non-browser callers; see RefreshTokenCookie for why this is an addition.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Set(HttpContext, refreshTokenString, refreshTokenExpiry);

        await Send.ResponseAsync(new Response
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry
        });
    }
}
