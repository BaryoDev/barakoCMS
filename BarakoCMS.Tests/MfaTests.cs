using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Threading;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using LoginResponse = barakoCMS.Features.Auth.Login.Response;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests;

/// <summary>
/// TOTP MFA end to end over real HTTP: enroll (setup -> enable), then a two-step login (password ->
/// challenge -> code -> tokens). Adversarial: wrong codes fail and count toward lockout, a TOTP can't be
/// replayed, recovery codes are single-use, disable needs a code, and setup requires authentication.
/// Each test instance uses a distinct client IP so it gets its own auth rate-limit bucket.
/// </summary>
[Collection("Sequential")]
public class MfaTests
{
    private const string Password = "P@ssword123!Ab";
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private readonly string _ip;

    public MfaTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _ip = $"198.51.100.{n % 250 + 1}"; // TEST-NET-2, reserved for documentation
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
    }

    private async Task<(Guid Id, string Username)> SeedUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var username = $"mfa-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        s.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = new List<Guid> { Guid.Parse("00000000-0000-0000-0000-000000000001") },
        });
        await s.SaveChangesAsync();
        return (id, username);
    }

    private HttpClient AuthedClient(Guid userId)
    {
        var token = _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
        var c = _factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
        c.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, _ip);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Seeds a user and completes TOTP enrollment. Returns the secret + recovery codes.</summary>
    private async Task<(Guid Id, string Secret, List<string> Recovery)> EnrollAsync()
    {
        var (id, _) = await SeedUserAsync();
        var authed = AuthedClient(id);

        var setup = await (await authed.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>();
        setup!.Secret.Should().NotBeNullOrEmpty();
        setup.OtpauthUri.Should().StartWith("otpauth://totp/");

        var totp = new Totp(Base32Encoding.ToBytes(setup.Secret));
        var enableRes = await authed.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = totp.ComputeTotp() });
        enableRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var enable = await enableRes.Content.ReadFromJsonAsync<MfaModels.EnableResponse>();
        enable!.RecoveryCodes.Should().HaveCount(10);

        return (id, setup.Secret, enable.RecoveryCodes);
    }

    private async Task<LoginResponse> LoginAsync(Guid id)
    {
        var (_, username) = (id, (await LoadUsernameAsync(id)));
        var res = await _client.PostAsJsonAsync("/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private async Task<string> LoadUsernameAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await s.LoadAsync<User>(id))!.Username;
    }

    [Fact]
    public async Task Enroll_Then_TwoStepLogin_IssuesTokens()
    {
        var (id, secret, _) = await EnrollAsync();

        // Status reflects enrollment.
        var status = await (await AuthedClient(id).GetAsync("/api/auth/mfa/status"))
            .Content.ReadFromJsonAsync<MfaModels.StatusResponse>();
        status!.Enabled.Should().BeTrue();

        // Password login now yields a challenge, not tokens.
        var login = await LoginAsync(id);
        login.RequiresMfa.Should().BeTrue();
        login.Token.Should().BeNullOrEmpty();
        login.MfaChallengeToken.Should().NotBeNullOrEmpty();

        // Completing the second step returns real tokens.
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var verifyRes = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login.MfaChallengeToken!, Code = totp.ComputeTotp() });
        verifyRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var verify = await verifyRes.Content.ReadFromJsonAsync<MfaModels.VerifyResponse>();
        verify!.Token.Should().NotBeNullOrEmpty();
        verify.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Verify_WrongCode_IsRejected()
    {
        var (id, _, _) = await EnrollAsync();
        var login = await LoginAsync(id);

        var res = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login.MfaChallengeToken!, Code = "000000" });
        res.StatusCode.Should().NotBe(HttpStatusCode.OK);
        res.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "should fail on the code, not the rate limit");
    }

    [Fact]
    public async Task Totp_CannotBeReplayed()
    {
        var (id, secret, _) = await EnrollAsync();
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        var login1 = await LoginAsync(id);
        var code = totp.ComputeTotp();
        var ok = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login1.MfaChallengeToken!, Code = code });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Same code again (fresh challenge) must be refused by the replay guard.
        var login2 = await LoginAsync(id);
        var replay = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login2.MfaChallengeToken!, Code = code });
        replay.StatusCode.Should().NotBe(HttpStatusCode.OK);
        replay.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RecoveryCode_Works_Once()
    {
        var (id, _, recovery) = await EnrollAsync();
        var code = recovery[0];

        var login1 = await LoginAsync(id);
        var ok = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login1.MfaChallengeToken!, Code = code });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var login2 = await LoginAsync(id);
        var reuse = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = login2.MfaChallengeToken!, Code = code });
        reuse.StatusCode.Should().NotBe(HttpStatusCode.OK, "a recovery code is single-use");
        reuse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task EmailOtpPath_AlsoRequiresMfa_NotBypassable()
    {
        var (id, secret, _) = await EnrollAsync();

        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            email = (await s.LoadAsync<User>(id))!.Email;
            // Seed a valid, unconsumed email code for this account (as if /otp/request had run).
            s.Store(new OtpCode
            {
                Id = Guid.NewGuid(),
                Email = email.ToLowerInvariant(),
                CodeHash = BCrypt.Net.BCrypt.HashPassword("123456"),
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await s.SaveChangesAsync();
        }

        // A correct email code must NOT mint tokens for an MFA-enabled account — it returns a challenge.
        var otpRes = await _client.PostAsJsonAsync("/api/auth/otp/verify",
            new barakoCMS.Features.Auth.Otp.OtpVerifyRequest { Email = email, Code = "123456" });
        otpRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var otp = await otpRes.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Otp.OtpVerifyResponse>();
        otp!.RequiresMfa.Should().BeTrue("mailbox possession alone can't satisfy MFA");
        otp.Token.Should().BeNullOrEmpty();
        otp.MfaChallengeToken.Should().NotBeNullOrEmpty();

        // The challenge still completes normally with a TOTP.
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var verify = await _client.PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = otp.MfaChallengeToken!, Code = totp.ComputeTotp() });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EnablingMfa_RevokesSessionsThatPredateIt()
    {
        // The risk this closes: an attacker who has hijacked a session on an account without MFA can
        // enrol their own authenticator and hold the account. Sessions established before MFA existed
        // must not survive it, or the enrolment is silent and permanent.
        var (id, _) = await SeedUserAsync();
        var authed = AuthedClient(id);

        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            tokenId = Guid.NewGuid();
            s.Store(new RefreshToken
            {
                Id = tokenId,
                Token = $"pre-mfa-{Guid.NewGuid():N}",
                UserId = id,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow,
                IsRevoked = false,
            });
            await s.SaveChangesAsync();
        }

        var setup = await (await authed.PostAsync("/api/auth/mfa/setup", null))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>();
        var totp = new Totp(Base32Encoding.ToBytes(setup!.Secret));
        var res = await authed.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = totp.ComputeTotp() });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _factory.Services.CreateScope();
        var q = check.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await q.LoadAsync<RefreshToken>(tokenId);
        stored!.IsRevoked.Should().BeTrue("a session opened before MFA was enabled must not outlive it");
        stored.RevokedReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Setup_RequiresAuthentication()
    {
        var res = await _client.PostAsync("/api/auth/mfa/setup", null);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disable_RequiresValidCode_ThenLoginNeedsNoMfa()
    {
        var (id, secret, _) = await EnrollAsync();
        var authed = AuthedClient(id);
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        var bad = await authed.PostAsJsonAsync("/api/auth/mfa/disable", new MfaModels.CodeRequest { Code = "000000" });
        bad.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var good = await authed.PostAsJsonAsync("/api/auth/mfa/disable", new MfaModels.CodeRequest { Code = totp.ComputeTotp() });
        good.StatusCode.Should().Be(HttpStatusCode.OK);

        // With MFA off, password login issues tokens directly.
        var login = await LoginAsync(id);
        login.RequiresMfa.Should().BeFalse();
        login.Token.Should().NotBeNullOrEmpty();
    }
}
