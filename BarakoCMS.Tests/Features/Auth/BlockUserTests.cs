using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BarakoCMS.ExternalAuth;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests.Features.Auth;

/// <summary>
/// A SuperAdmin can block an account (#1366). A blocked user cannot sign in by any method, cannot
/// refresh, switch tenant or step up, gets no codes or links, and every token and owned API key
/// stops working. Unblocking restores sign-in and the keys, and nothing the block ended.
/// </summary>
[Collection("Integration1")]
public class BlockUserTests
{
    private const string Password = "P@ssword123!Ab";

    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public BlockUserTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- helpers -------------------------------------------------------------------------

    private static string NextIp() => $"2001:db8:1366:{Interlocked.Increment(ref _ip):x}::1";

    private HttpClient Client(string? bearer = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return client;
    }

    private sealed record Seeded(Guid Id, string Username, string Email);

    /// <summary>A user with no roles, so a SuperAdmin may block them.</summary>
    private async Task<Seeded> SeedUserAsync(params Guid[] roleIds)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var username = $"blk-{id:N}";
        var email = $"{username}@example.com";
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = roleIds.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return new Seeded(id, username, email);
    }

    private sealed record Admin(HttpClient Client, Guid Id);

    private async Task<Admin> SuperAdminAsync()
    {
        var token = await _fixture.StoredUserTokenAsync("SuperAdmin");
        var id = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "UserId").Value);
        return new Admin(Client(token), id);
    }

    private async Task<HttpStatusCode> BlockAsync(Seeded user) =>
        (await (await SuperAdminAsync()).Client.PostAsync($"/api/users/{user.Id}/block", null, Ct)).StatusCode;

    private async Task<HttpStatusCode> UnblockAsync(Seeded user) =>
        (await (await SuperAdminAsync()).Client.PostAsync($"/api/users/{user.Id}/unblock", null, Ct)).StatusCode;

    private async Task BlockedAsync(Seeded user) =>
        (await BlockAsync(user)).Should().Be(HttpStatusCode.OK);

    /// <summary>
    /// Writes only the stored flag. Nothing is revoked and this instance's cached epoch is left as it
    /// was (warmed first), so what refuses the user afterwards is the check under test and not the
    /// revocation or the epoch the block endpoint would also have set.
    /// </summary>
    private async Task StampBlockedOnlyAsync(Seeded user)
    {
        using var scope = _fixture.Services.CreateScope();
        _ = await scope.ServiceProvider.GetRequiredService<ISessionEpochService>().ValidFromAsync(user.Id, Ct);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Patch<User>(user.Id).Set(u => u.BlockedAt, DateTime.UtcNow);
        await session.SaveChangesAsync(Ct);
    }

    private Task<HttpResponseMessage> LoginRawAsync(string username, string password) =>
        Client().PostAsJsonAsync("/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = username, Password = password }, Ct);

    private sealed record Pair(string Token, string Refresh);

    private async Task<Pair> LoginAsync(Seeded user)
    {
        var response = await LoginRawAsync(user.Username, Password);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!;
        return new Pair(body.Token!, body.RefreshToken!);
    }

    private Task<HttpResponseMessage> WhoAmIAsync(string token) => Client(token).GetAsync("/api/me", Ct);

    private Task<HttpResponseMessage> RefreshAsync(string refresh) =>
        Client().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }, Ct);

    private async Task SeedOtpAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = email.ToLowerInvariant(),
            CodeHash = BCrypt.Net.BCrypt.HashPassword("123456"),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await session.SaveChangesAsync(Ct);
    }

    private Task<HttpResponseMessage> VerifyOtpAsync(string email, string code = "123456") =>
        Client().PostAsJsonAsync("/api/auth/otp/verify",
            new barakoCMS.Features.Auth.Otp.OtpVerifyRequest { Email = email, Code = code }, Ct);

    private async Task<string> SeedLinkAsync(string email)
    {
        var row = new MagicLinkToken
        {
            Email = email.ToLowerInvariant(),
            TenantSlug = Tenant.DefaultSlug,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
        var (token, hash) = EmailVerificationToken.Create(row.Id);
        row.TokenHash = hash;
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(row);
        await session.SaveChangesAsync(Ct);
        return token;
    }

    private Task<HttpResponseMessage> ConfirmLinkAsync(string token) =>
        Client().PostAsJsonAsync("/api/auth/magic-link/confirm", new { token }, Ct);

    private async Task<(Seeded User, string Secret)> EnrolledUserAsync()
    {
        var user = await SeedUserAsync();
        var authed = Client(_fixture.CreateToken(["SuperAdmin"], user.Id.ToString()));
        var setup = await (await authed.PostAsync("/api/auth/mfa/setup", null, Ct))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>(Ct);
        var totp = new Totp(Base32Encoding.ToBytes(setup!.Secret));
        (await authed.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = totp.ComputeTotp() }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, setup.Secret);
    }

    private async Task<string> MfaChallengeAsync(Seeded user)
    {
        var login = await LoginRawAsync(user.Username, Password);
        var challenge = (await login.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!;
        challenge.RequiresMfa.Should().BeTrue();
        return challenge.MfaChallengeToken!;
    }

    private Task<HttpResponseMessage> VerifyMfaAsync(string challenge, string secret) =>
        Client().PostAsJsonAsync("/api/auth/mfa/verify",
            new MfaModels.VerifyRequest
            {
                ChallengeToken = challenge,
                Code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(),
            }, Ct);

    private static Microsoft.AspNetCore.Http.DefaultHttpContext SocialContext()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.77");
        return context;
    }

    private async Task<SocialSignIn.Tokens> SocialAsync(string email, string provider, string? subject = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var (tokens, _) = await SocialSignIn.IssueForProviderAsync(
            services.GetRequiredService<IDocumentSession>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            SocialContext(),
            provider,
            subject ?? "sub-" + Guid.NewGuid().ToString("N"),
            email,
            emailVerified: true,
            Tenant.DefaultSlug,
            Ct);
        return tokens;
    }

    private async Task<SocialSignIn.Tokens> OidcAsync(string email, string? subject = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var identity = new OidcIdentity(
            "https://issuer.example", subject ?? "sub-" + Guid.NewGuid().ToString("N"), email, true, null, null);
        var (tokens, _) = await SocialSignIn.IssueForIdentityAsync(
            services.GetRequiredService<IDocumentSession>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            SocialContext(),
            identity,
            "native",
            Tenant.DefaultSlug,
            Ct);
        return tokens;
    }

    private async Task<int> RefreshRowsAsync(Guid userId, bool liveOnly)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<RefreshToken>().Where(t => t.UserId == userId).ToListAsync(Ct))
            .Count(t => !liveOnly || !t.IsRevoked);
    }

    private async Task<User> StoredAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id, Ct))!;
    }

    private static string Comparable(string body)
    {
        var node = JsonNode.Parse(body);
        if (node is JsonObject obj)
        {
            obj.Remove("traceId");
        }
        return node?.ToJsonString() ?? string.Empty;
    }

    private async Task<int> RefusedLoginsAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "auth.login.blocked" && e.ActorUserId == userId)
            .ToListAsync(Ct);
        rows.Should().OnlyContain(r => r.Metadata != null && r.Metadata["reason"].ToString() == "account_blocked");
        return rows.Count;
    }

    /// <summary>
    /// Runs one refused sign-in and expects it to leave an auth.login.blocked row. The token issuer
    /// would refuse most of these anyway, so the row is what proves the path's own check ran.
    /// </summary>
    private async Task AuditedAsync(Guid userId, string path, Func<Task> attempt)
    {
        var before = await RefusedLoginsAsync(userId);
        await attempt();
        (await RefusedLoginsAsync(userId)).Should().Be(before + 1, path + " is audited as account_blocked");
    }

    private async Task StoreRefreshAsync(Guid userId, string raw)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = raw,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(string action, Guid target)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == action && e.TargetId == target.ToString())
            .ToListAsync(Ct)).ToList();
    }

    /// <summary>A global role that is not a platform role and carries the given capabilities.</summary>
    private async Task<Role> RoleAsync(params string[] capabilities)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"blk-role-{Guid.NewGuid():N}"[..20],
            Permissions = new(),
            SystemCapabilities = capabilities.ToList(),
        };
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<string> StoreKeyAsync(Guid ownerId, Guid? principalId = null)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "blk",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            ServicePrincipalId = principalId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = ["cap:view_jobs"],
        });
        await session.SaveChangesAsync(Ct);
        return secret;
    }

    private async Task<HttpStatusCode> JobsWithAsync(string secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        req.Headers.Add(TestRemoteIpFilter.Header, NextIp());
        return (await _fixture.CreateClient().SendAsync(req, Ct)).StatusCode;
    }

    // ---- tests ---------------------------------------------------------------------------

    [Fact]
    public async Task Blocked_user_cannot_sign_in_by_any_path()
    {
        // Password.
        var password = await SeedUserAsync();
        await LoginAsync(password);
        await BlockedAsync(password);
        await AuditedAsync(password.Id, "password", async () =>
            (await LoginRawAsync(password.Username, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized));

        // Emailed code.
        var code = await SeedUserAsync();
        await SeedOtpAsync(code.Email);
        await BlockedAsync(code);
        await AuditedAsync(code.Id, "code", async () =>
            (await VerifyOtpAsync(code.Email)).StatusCode.Should().Be(HttpStatusCode.BadRequest));

        // Emailed link.
        var link = await SeedUserAsync();
        var token = await SeedLinkAsync(link.Email);
        await BlockedAsync(link);
        await AuditedAsync(link.Id, "link", async () =>
            (await ConfirmLinkAsync(token)).StatusCode.Should().Be(HttpStatusCode.BadRequest));

        // Second factor: the password step is refused before any challenge exists.
        var (mfaUser, _) = await EnrolledUserAsync();
        await BlockedAsync(mfaUser);
        var mfaLogin = await LoginRawAsync(mfaUser.Username, Password);
        mfaLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await mfaLogin.Content.ReadAsStringAsync(Ct)).Should().NotContain("mfaChallengeToken");

        // Each social provider path, for a person already linked and for an address matched.
        foreach (var provider in new[]
                 {
                     ExternalProviders.Google, ExternalProviders.GitHub, ExternalProviders.Facebook, ExternalProviders.LinkedIn,
                 })
        {
            var social = await SeedUserAsync();
            var subject = "sub-" + Guid.NewGuid().ToString("N");
            (await SocialAsync(social.Email, provider, subject)).Allowed.Should().BeTrue(provider + " before the block");
            await BlockedAsync(social);

            await AuditedAsync(social.Id, provider + " by its link", async () =>
                (await SocialAsync(social.Email, provider, subject)).Allowed.Should().BeFalse());
            await AuditedAsync(social.Id, provider + " by address", async () =>
                (await SocialAsync(social.Email, provider)).Allowed.Should().BeFalse());
        }

        // The OIDC id token grant.
        var oidc = await SeedUserAsync();
        var oidcSubject = "sub-" + Guid.NewGuid().ToString("N");
        (await OidcAsync(oidc.Email, oidcSubject)).Allowed.Should().BeTrue("before the block");
        await BlockedAsync(oidc);
        await AuditedAsync(oidc.Id, "oidc by its link", async () =>
            (await OidcAsync(oidc.Email, oidcSubject)).Allowed.Should().BeFalse());
        await AuditedAsync(oidc.Id, "oidc by address", async () =>
            (await OidcAsync(oidc.Email)).Allowed.Should().BeFalse());

        // Nothing above left a live session behind for a blocked user.
        (await RefreshRowsAsync(password.Id, liveOnly: true)).Should().Be(0);
        (await RefreshRowsAsync(code.Id, liveOnly: true)).Should().Be(0);
        (await RefreshRowsAsync(link.Id, liveOnly: true)).Should().Be(0);
        (await RefreshRowsAsync(oidc.Id, liveOnly: true)).Should().Be(0);
    }

    [Fact]
    public async Task Blocked_sign_in_answers_like_a_wrong_password()
    {
        var blocked = await SeedUserAsync();
        var other = await SeedUserAsync();
        var unknown = $"nobody{Guid.NewGuid():N}"[..20];
        await BlockedAsync(blocked);

        var responses = new[]
        {
            await LoginRawAsync(unknown, Password),
            await LoginRawAsync(other.Username, "WrongPassword123!"),
            await LoginRawAsync(blocked.Username, Password),
        };
        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            bodies.Add(Comparable(await response.Content.ReadAsStringAsync(Ct)));
        }
        bodies.Should().HaveCount(3);
        bodies.Distinct().Should().ContainSingle("the three answers are the same: {0}", string.Join(" | ", bodies));

        // The same timing: the blocked attempt pays a password hash like a wrong password does.
        var wrong = new List<TimeSpan>();
        var refused = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            await LoginRawAsync(other.Username, "WrongPassword123!");
            wrong.Add(clock.Elapsed);
            clock.Restart();
            await LoginRawAsync(blocked.Username, Password);
            refused.Add(clock.Elapsed);
        }
        wrong.Should().HaveCount(3);
        refused.OrderBy(t => t).ElementAt(1).Should().BeGreaterThan(
            TimeSpan.FromTicks(wrong.OrderBy(t => t).ElementAt(1).Ticks / 2),
            "a blocked attempt that skips the hash answers measurably faster than a wrong password");

        // The code and link answers match a wrong code and an unknown link.
        var blockedCode = await SeedUserAsync();
        var wrongCode = await SeedUserAsync();
        await SeedOtpAsync(blockedCode.Email);
        await SeedOtpAsync(wrongCode.Email);
        await BlockedAsync(blockedCode);
        var blockedOtp = await VerifyOtpAsync(blockedCode.Email);
        var wrongOtp = await VerifyOtpAsync(wrongCode.Email, "000000");
        blockedOtp.StatusCode.Should().Be(wrongOtp.StatusCode);
        Comparable(await blockedOtp.Content.ReadAsStringAsync(Ct))
            .Should().Be(Comparable(await wrongOtp.Content.ReadAsStringAsync(Ct)));

        var blockedLink = await SeedUserAsync();
        var linkToken = await SeedLinkAsync(blockedLink.Email);
        await BlockedAsync(blockedLink);
        var blockedConfirm = await ConfirmLinkAsync(linkToken);
        var unknownConfirm = await ConfirmLinkAsync(EmailVerificationToken.Create(Guid.NewGuid()).Token);
        blockedConfirm.StatusCode.Should().Be(unknownConfirm.StatusCode);
        Comparable(await blockedConfirm.Content.ReadAsStringAsync(Ct))
            .Should().Be(Comparable(await unknownConfirm.Content.ReadAsStringAsync(Ct)));

        // The attempt is audited as a blocked one, with its reason.
        var audit = await AuditAsync("auth.login.blocked", blocked.Id);
        using var scope = _fixture.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "auth.login.blocked" && e.ActorUserId == blocked.Id)
            .ToListAsync(Ct);
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Metadata != null && r.Metadata["reason"].ToString() == "account_blocked");
        audit.Should().BeEmpty("the attempt names the user as actor, not as target");
    }

    [Fact]
    public async Task Code_and_link_requests_send_nothing_when_blocked()
    {
        var blocked = await SeedUserAsync();
        var control = await SeedUserAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";
        await BlockedAsync(blocked);

        var blockedCode = await Client().PostAsJsonAsync("/api/auth/otp/request", new { email = blocked.Email }, Ct);
        var unknownCode = await Client().PostAsJsonAsync("/api/auth/otp/request", new { email = unknown }, Ct);
        var blockedLink = await Client().PostAsJsonAsync("/api/auth/magic-link/request", new { email = blocked.Email }, Ct);
        var unknownLink = await Client().PostAsJsonAsync("/api/auth/magic-link/request", new { email = unknown }, Ct);
        await Client().PostAsJsonAsync("/api/auth/otp/request", new { email = control.Email }, Ct);
        await Client().PostAsJsonAsync("/api/auth/magic-link/request", new { email = control.Email }, Ct);

        blockedCode.StatusCode.Should().Be(unknownCode.StatusCode);
        (await blockedCode.Content.ReadAsStringAsync(Ct)).Should().Be(await unknownCode.Content.ReadAsStringAsync(Ct));
        blockedLink.StatusCode.Should().Be(unknownLink.StatusCode);
        (await blockedLink.Content.ReadAsStringAsync(Ct)).Should().Be(await unknownLink.Content.ReadAsStringAsync(Ct));

        // The control proves the queue ran, so an empty result for the blocked address means something.
        var mail = _fixture.Email;
        (await RecordingEmailService.WaitForAsync(
            () => mail.Messages.Count(m => m.To == control.Email.ToLowerInvariant()) >= 2))
            .Should().BeTrue("a user who is not blocked gets a code and a link");
        mail.Messages.Where(m => m.To == blocked.Email.ToLowerInvariant()).Should().BeEmpty();

        using var scope = _fixture.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.Query<OtpCode>().Where(o => o.Email == control.Email.ToLowerInvariant()).ToListAsync(Ct))
            .Should().NotBeEmpty();
        (await query.Query<OtpCode>().Where(o => o.Email == blocked.Email.ToLowerInvariant()).ToListAsync(Ct))
            .Should().BeEmpty("no code is stored for a blocked address");
        (await query.Query<MagicLinkToken>().Where(t => t.Email == blocked.Email.ToLowerInvariant()).ToListAsync(Ct))
            .Should().BeEmpty("no link is stored for a blocked address");
    }

    [Fact]
    public async Task Challenge_started_before_the_block_does_not_finish()
    {
        // Code.
        var code = await SeedUserAsync();
        await SeedOtpAsync(code.Email);
        await BlockedAsync(code);
        var codeBefore = await RefusedLoginsAsync(code.Id);
        var otp = await VerifyOtpAsync(code.Email);
        otp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RefusedLoginsAsync(code.Id)).Should().Be(codeBefore + 1, "the code path is audited as account_blocked");
        (await otp.Content.ReadAsStringAsync(Ct)).Should().NotContain("refreshToken\":\"");
        (await RefreshRowsAsync(code.Id, liveOnly: false)).Should().Be(0);

        // Link.
        var link = await SeedUserAsync();
        var token = await SeedLinkAsync(link.Email);
        await BlockedAsync(link);
        var linkBefore = await RefusedLoginsAsync(link.Id);
        (await ConfirmLinkAsync(token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RefusedLoginsAsync(link.Id)).Should().Be(linkBefore + 1, "the link path is audited as account_blocked");
        (await RefreshRowsAsync(link.Id, liveOnly: false)).Should().Be(0);

        // Second factor: the password step passed, the block came before the code.
        var (mfaUser, secret) = await EnrolledUserAsync();
        var challenge = await MfaChallengeAsync(mfaUser);
        await BlockedAsync(mfaUser);
        var mfaBefore = await RefusedLoginsAsync(mfaUser.Id);
        var verify = await VerifyMfaAsync(challenge, secret);
        verify.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RefusedLoginsAsync(mfaUser.Id)).Should().Be(mfaBefore + 1, "the MFA path is audited as account_blocked");
        (await verify.Content.ReadAsStringAsync(Ct)).Should().NotContain("refreshToken\":\"");
        (await RefreshRowsAsync(mfaUser.Id, liveOnly: false)).Should().Be(0);
    }

    [Fact]
    public async Task Refresh_switch_and_step_up_are_refused_when_blocked()
    {
        var user = await SeedUserAsync();
        var session = await LoginAsync(user);
        (await WhoAmIAsync(session.Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (mfaUser, secret) = await EnrolledUserAsync();
        var stepUpToken = _fixture.CreateToken(["SuperAdmin"], mfaUser.Id.ToString());
        (await Client(stepUpToken).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Only the stored flag is set, and the cached epoch still says nothing happened.
        await StampBlockedOnlyAsync(user);
        await StampBlockedOnlyAsync(mfaUser);

        // Refresh answers as it does for a token it does not know, and does not rotate.
        var refresh = await RefreshAsync(session.Refresh);
        var unknown = await RefreshAsync(Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Comparable(await refresh.Content.ReadAsStringAsync(Ct))
            .Should().Be(Comparable(await unknown.Content.ReadAsStringAsync(Ct)));
        (await RefreshRowsAsync(user.Id, liveOnly: true)).Should().Be(1, "the refusal did not rotate the token");

        // Tenant switch.
        var switched = await Client(session.Token).PostAsJsonAsync("/api/me/switch", new { tenant = "default" }, Ct);
        switched.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Step-up.
        var totp = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        var stepUp = await Client(stepUpToken).PostAsJsonAsync("/api/auth/mfa/step-up", new MfaModels.CodeRequest { Code = totp }, Ct);
        stepUp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        (await stepUp.Content.ReadAsStringAsync(Ct)).Should().NotContain("stepUpToken\":\"");

        // The issuer reads the stored row, so a caller holding a copy loaded before the block is refused.
        using var scope = _fixture.Services.CreateScope();
        var stale = new User { Id = user.Id, Username = user.Username, Email = user.Email };
        var issued = await scope.ServiceProvider.GetRequiredService<ITokenIssuer>()
            .IssueAccessTokenAsync(stale, Tenant.DefaultSlug, ct: Ct);
        issued.Allowed.Should().BeFalse("the issuer checks the stored account, not the object it was handed");
    }

    [Fact]
    public async Task Token_issued_during_the_block_is_not_accepted_after_it()
    {
        var user = await SeedUserAsync();
        var session = await LoginAsync(user);

        // Warm this instance's cached epoch, the way an earlier request would have.
        (await WhoAmIAsync(session.Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await BlockedAsync(user);

        // The epoch the block stamped is put back to before the token's iat, which is what a token
        // minted between the stamp and the commit would face. Only the block itself refuses it now.
        using (var scope = _fixture.Services.CreateScope())
        {
            var write = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            write.Patch<User>(user.Id).Set(u => u.TokensValidFrom, (DateTime?)DateTime.UtcNow.AddMinutes(-5));
            await write.SaveChangesAsync(Ct);
            scope.ServiceProvider.GetRequiredService<ISessionEpochService>().Invalidate(user.Id);
        }

        (await WhoAmIAsync(session.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        (await UnblockAsync(user)).Should().Be(HttpStatusCode.OK);

        (await WhoAmIAsync(session.Token)).StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "a token from before the block does not come back with the account");
    }

    [Fact]
    public async Task Unblock_drops_the_cached_blocked_epoch()
    {
        var user = await SeedUserAsync();
        var old = await LoginAsync(user);

        await BlockedAsync(user);
        (await WhoAmIAsync(old.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "this caches the blocked epoch");

        await UnblockAsync(user);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        var fresh = await LoginAsync(user);

        (await WhoAmIAsync(fresh.Token)).StatusCode.Should().Be(
            HttpStatusCode.OK, "the cached far-future epoch did not survive the unblock");
    }

    [Fact]
    public async Task Refresh_row_stored_during_the_block_does_not_work_after_unblock()
    {
        var user = await SeedUserAsync();
        await BlockedAsync(user);

        // A sign-in that committed after the block's revoke query leaves a row like this.
        var raw = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        await StoreRefreshAsync(user.Id, raw);

        (await UnblockAsync(user)).Should().Be(HttpStatusCode.OK);
        (await RefreshRowsAsync(user.Id, liveOnly: true)).Should().Be(0);
        (await RefreshAsync(raw)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_repeated_block_revokes_again_without_a_second_audit()
    {
        var user = await SeedUserAsync();
        await BlockedAsync(user);
        await StoreRefreshAsync(user.Id, Convert.ToBase64String(Guid.NewGuid().ToByteArray()));

        await BlockedAsync(user);

        (await RefreshRowsAsync(user.Id, liveOnly: true)).Should().Be(0);
        (await AuditAsync("auth.account.blocked", user.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_blocks_and_unblocks_audit_once()
    {
        var user = await SeedUserAsync();
        var admin = await SuperAdminAsync();

        var blocks = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => admin.Client.PostAsync($"/api/users/{user.Id}/block", null, Ct)));
        blocks.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await AuditAsync("auth.account.blocked", user.Id)).Should().ContainSingle();

        var unblocks = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => admin.Client.PostAsync($"/api/users/{user.Id}/unblock", null, Ct)));
        unblocks.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await AuditAsync("auth.account.unblocked", user.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Owned_api_key_is_refused_while_blocked_and_works_after_unblock()
    {
        var viewer = await RoleAsync(SystemCapabilities.ViewJobs);
        var owner = await SeedUserAsync(viewer.Id);
        var key = await StoreKeyAsync(owner.Id);

        (await JobsWithAsync(key)).Should().Be(HttpStatusCode.OK);

        await BlockedAsync(owner);
        (await JobsWithAsync(key)).Should().Be(HttpStatusCode.Unauthorized);

        (await UnblockAsync(owner)).Should().Be(HttpStatusCode.OK);
        (await JobsWithAsync(key)).Should().Be(HttpStatusCode.OK, "unblocking brings the owner's keys back");
    }

    [Fact]
    public async Task Service_principal_key_keeps_working()
    {
        var viewer = await RoleAsync(SystemCapabilities.ViewJobs);
        var creator = await SeedUserAsync(viewer.Id);
        var principal = new ServicePrincipal
        {
            Id = Guid.NewGuid(),
            TenantSlug = Tenant.DefaultSlug,
            Name = $"blk-{Guid.NewGuid():N}"[..12],
            RoleIds = [viewer.Id],
            CreatedBy = creator.Id,
        };
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(principal);
            await session.SaveChangesAsync(Ct);
        }

        var principalKey = await StoreKeyAsync(creator.Id, principal.Id);
        var ownKey = await StoreKeyAsync(creator.Id);
        (await JobsWithAsync(principalKey)).Should().Be(HttpStatusCode.OK);
        (await JobsWithAsync(ownKey)).Should().Be(HttpStatusCode.OK);

        await BlockedAsync(creator);

        (await JobsWithAsync(ownKey)).Should().Be(HttpStatusCode.Unauthorized, "the control: an owned key stops");
        (await JobsWithAsync(principalKey)).Should().Be(HttpStatusCode.OK, "a principal key does not load its creator");
    }

    [Fact]
    public async Task Only_a_global_super_admin_can_block_and_not_self_or_a_super_admin()
    {
        var target = await SeedUserAsync();

        // No capability at all.
        var nobody = await SeedUserAsync();
        var plain = Client(_fixture.CreateToken(["Nobody"], nobody.Id.ToString()));
        (await plain.PostAsync($"/api/users/{target.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // manage_users in a global role is not enough: blocking is for a SuperAdmin.
        var manage = await RoleAsync(SystemCapabilities.ManageUsers);
        var manager = await SeedUserAsync(manage.Id);
        var managerClient = Client(_fixture.CreateToken([manage.Name], manager.Id.ToString()));
        (await managerClient.PostAsync($"/api/users/{target.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsync($"/api/users/{target.Id}/unblock", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(target.Id)).BlockedAt.Should().BeNull();

        var admin = await SuperAdminAsync();

        // Not self.
        (await admin.Client.PostAsync($"/api/users/{admin.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(admin.Id)).BlockedAt.Should().BeNull();

        // Not another SuperAdmin.
        var otherAdmin = await SeedUserAsync(barakoCMS.Data.DataSeeder.SuperAdminRoleId);
        (await admin.Client.PostAsync($"/api/users/{otherAdmin.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(otherAdmin.Id)).BlockedAt.Should().BeNull();

        // Not a user that does not exist. The 404 names it, so a console can tell it from a server
        // with no block route, which answers 404 with no body.
        foreach (var action in new[] { "block", "unblock" })
        {
            var missing = await admin.Client.PostAsync($"/api/users/{Guid.NewGuid()}/{action}", null, Ct);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await missing.Content.ReadAsStringAsync(Ct)).Should().Contain("No account has that id.");
        }

        // And a SuperAdmin can.
        (await admin.Client.PostAsync($"/api/users/{target.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await StoredAsync(target.Id);
        stored.BlockedAt.Should().NotBeNull();
        stored.BlockedBy.Should().Be(admin.Id);

        (await admin.Client.PostAsync($"/api/users/{target.Id}/unblock", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = await StoredAsync(target.Id);
        restored.BlockedAt.Should().BeNull();
        restored.BlockedBy.Should().BeNull();
    }

    [Fact]
    public async Task Block_and_unblock_are_audited()
    {
        var user = await SeedUserAsync();
        var admin = await SuperAdminAsync();

        (await admin.Client.PostAsync($"/api/users/{user.Id}/block", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await AuditAsync("auth.account.blocked", user.Id);
        blocked.Should().ContainSingle().Which.ActorUserId.Should().Be(admin.Id);
        blocked[0].TargetType.Should().Be("User");

        (await admin.Client.PostAsync($"/api/users/{user.Id}/unblock", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var unblocked = await AuditAsync("auth.account.unblocked", user.Id);
        unblocked.Should().ContainSingle().Which.ActorUserId.Should().Be(admin.Id);

        // A repeat changes nothing and is not audited again.
        (await admin.Client.PostAsync($"/api/users/{user.Id}/unblock", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditAsync("auth.account.unblocked", user.Id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Unblock_leaves_old_tokens_dead_and_lockout_fields_alone()
    {
        var user = await SeedUserAsync();
        var old = await LoginAsync(user);
        (await WhoAmIAsync(old.Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var lockedUntil = DateTime.UtcNow.AddMinutes(-5);
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Patch<User>(user.Id).Set(u => u.FailedLoginAttempts, 3);
            session.Patch<User>(user.Id).Set(u => u.LockoutUntil, (DateTime?)lockedUntil);
            await session.SaveChangesAsync(Ct);
        }

        await BlockedAsync(user);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        (await UnblockAsync(user)).Should().Be(HttpStatusCode.OK);

        var stored = await StoredAsync(user.Id);
        stored.BlockedAt.Should().BeNull();
        stored.FailedLoginAttempts.Should().Be(3);
        stored.LockoutUntil.Should().BeCloseTo(lockedUntil, TimeSpan.FromSeconds(1));

        (await WhoAmIAsync(old.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the block ended this token");
        (await RefreshAsync(old.Refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the block ended this session");

        // Signing in works again, with a token the epoch accepts.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        var fresh = await LoginAsync(user);
        (await WhoAmIAsync(fresh.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
