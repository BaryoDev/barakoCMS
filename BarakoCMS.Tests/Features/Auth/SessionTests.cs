using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OtpNet;
using Xunit;
using MfaModels = barakoCMS.Features.Auth.Mfa;

namespace BarakoCMS.Tests.Features.Auth;

/// <summary>
/// Each sign-in is a session with its own id (#1364). The id is on the refresh row and in every
/// access token as <c>sid</c>, and ending it revokes that session and no other.
/// </summary>
/// <remarks>
/// These read the refresh rows as stored JSON and end a session by writing the same two documents
/// the session end writes (a RevokedToken keyed <c>sid:&lt;id&gt;</c> and a revoked row with reason
/// <c>session_revoked</c>), so the file builds before the change exists and fails on what it checks.
/// </remarks>
[Collection("Integration1")]
public class SessionTests
{
    private const string Password = "P@ssword123!Ab";
    private const string ChromeOnMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string FirefoxOnLinux = "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0";

    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public SessionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- helpers -------------------------------------------------------------------------

    private static string NextIp() => $"2001:db8:1364:{Interlocked.Increment(ref _ip):x}::1";

    private HttpClient Client(string? bearer = null, string? userAgent = null, string? ip = null, string? tenant = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? NextIp());
        if (userAgent is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        if (tenant is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private sealed record Seeded(Guid Id, string Username, string Email);

    private async Task<Seeded> SeedUserAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var username = $"sess-{id:N}";
        var email = $"{username}@example.com";
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [Guid.Parse("00000000-0000-0000-0000-000000000001")],
        });
        await session.SaveChangesAsync(Ct);
        return new Seeded(id, username, email);
    }

    private sealed record Pair(string Token, string Refresh);

    private async Task<Pair> LoginAsync(Seeded user, string? userAgent = ChromeOnMac, string? ip = null, string? tenant = null)
    {
        var response = await Client(userAgent: userAgent, ip: ip, tenant: tenant).PostAsJsonAsync(
            "/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = user.Username, Password = Password }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!;
        return new Pair(body.Token!, body.RefreshToken!);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refresh, string? ip = null, string? userAgent = ChromeOnMac) =>
        await Client(userAgent: userAgent, ip: ip).PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }, Ct);

    private async Task<Pair> RefreshedAsync(string refresh, string? ip = null)
    {
        var response = await RefreshAsync(refresh, ip);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Pair(body.GetProperty("token").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> WhoAmIAsync(string token, string? tenant = null) =>
        Client(bearer: token, tenant: tenant).GetAsync("/api/me", Ct);

    private static string? Sid(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.FirstOrDefault(c => c.Type == "sid")?.Value;

    /// <summary>One stored refresh row, read as JSON so this file does not need the new properties.</summary>
    private sealed class Row(JsonElement json)
    {
        public string? Text(string name)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
            }
            return null;
        }

        public Guid Id => Guid.Parse(Text("Id")!);
        public string? SessionId => Text("SessionId");
        public string? SessionStartedAt => Text("SessionStartedAt");
        public string? Device => Text("Device");
        public string? DeviceId => Text("DeviceId");
        public string? SignInIp => Text("SignInIp");
        public string? SignInMethod => Text("SignInMethod");
        public string? LastSeenAt => Text("LastSeenAt");
        public string? LastSeenIp => Text("LastSeenIp");
        public bool IsRevoked => Text("IsRevoked") == "True";
        public string? RevokedReason => Text("RevokedReason");
        public bool IsBlank(string? value) => string.IsNullOrEmpty(value);
    }

    private async Task<List<Row>> RowsAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var json = await session.Query<RefreshToken>().Where(t => t.UserId == userId).ToJsonArray(Ct);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => new Row(e.Clone())).ToList();
    }

    private async Task<Row> LiveRowAsync(Guid userId, string? sessionId = null)
    {
        var rows = (await RowsAsync(userId)).Where(r => !r.IsRevoked && (sessionId is null || r.SessionId == sessionId)).ToList();
        rows.Should().ContainSingle("a session holds one live refresh row");
        return rows[0];
    }

    /// <summary>
    /// Ends the session the way the service does: the revoke key and the revoked refresh row.
    /// </summary>
    private async Task EndSessionAsync(Guid userId, string sessionId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await barakoCMS.Infrastructure.Auth.SessionRevocation.EndAsync(
            session, null, userId, Guid.Parse(sessionId), Ct);
    }

    private async Task<(Seeded User, string Secret)> EnrolledUserAsync()
    {
        var user = await SeedUserAsync();
        var authed = Client(bearer: _fixture.CreateToken(["SuperAdmin"], user.Id.ToString()));
        var setup = await (await authed.PostAsync("/api/auth/mfa/setup", null, Ct))
            .Content.ReadFromJsonAsync<MfaModels.SetupResponse>(Ct);
        var totp = new Totp(Base32Encoding.ToBytes(setup!.Secret));
        (await authed.PostAsJsonAsync("/api/auth/mfa/enable", new MfaModels.CodeRequest { Code = totp.ComputeTotp() }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (user, setup.Secret);
    }

    private async Task<Pair> PasswordThenMfaAsync(Seeded user, string secret)
    {
        var login = await Client(userAgent: ChromeOnMac).PostAsJsonAsync(
            "/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = user.Username, Password = Password }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!;
        challenge.RequiresMfa.Should().BeTrue();
        return await VerifyMfaAsync(challenge.MfaChallengeToken!, secret);
    }

    private async Task<Pair> VerifyMfaAsync(string challenge, string secret)
    {
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var verify = await Client(userAgent: ChromeOnMac).PostAsJsonAsync(
            "/api/auth/mfa/verify",
            new MfaModels.VerifyRequest { ChallengeToken = challenge, Code = totp.ComputeTotp() }, Ct);
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await verify.Content.ReadFromJsonAsync<MfaModels.VerifyResponse>(Ct))!;
        return new Pair(body.Token, body.RefreshToken);
    }

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

    private async Task<HttpResponseMessage> VerifyOtpAsync(string email) =>
        await Client(userAgent: ChromeOnMac).PostAsJsonAsync(
            "/api/auth/otp/verify", new barakoCMS.Features.Auth.Otp.OtpVerifyRequest { Email = email, Code = "123456" }, Ct);

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

    private async Task<HttpResponseMessage> ConfirmLinkAsync(string token) =>
        await Client(userAgent: ChromeOnMac).PostAsJsonAsync("/api/auth/magic-link/confirm", new { token }, Ct);

    private static async Task<(string Token, string Refresh)> BodyTokensAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("token").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext SocialContext(string userAgent, string? ip = null)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = userAgent;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip ?? "198.51.100.77");
        return context;
    }

    private async Task<SocialSignIn.Tokens> SocialAsync(string email, string provider, string userAgent = ChromeOnMac)
    {
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var (tokens, _) = await SocialSignIn.IssueForProviderAsync(
            services.GetRequiredService<IDocumentSession>(),
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            SocialContext(userAgent),
            provider,
            "sub-" + Guid.NewGuid().ToString("N"),
            email,
            emailVerified: true,
            Tenant.DefaultSlug,
            Ct);
        return tokens;
    }

    private async Task<Guid> UserIdByEmailAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<User>().FirstAsync(u => u.Email == email, Ct)).Id;
    }

    private async Task AssertStartedSessionAsync(Guid userId, string token, string path)
    {
        var sid = Sid(token);
        sid.Should().NotBeNullOrEmpty($"{path} mints an access token with a sid");
        Guid.TryParse(sid, out _).Should().BeTrue();
        (await RowsAsync(userId)).Should().Contain(r => r.SessionId == sid, $"{path} stores the session id on the refresh row");
    }

    // ---- tests ---------------------------------------------------------------------------

    [Fact]
    public async Task Each_sign_in_path_starts_a_session()
    {
        // Password.
        var password = await SeedUserAsync();
        await AssertStartedSessionAsync(password.Id, (await LoginAsync(password)).Token, "password");

        // Emailed code.
        var code = await SeedUserAsync();
        await SeedOtpAsync(code.Email);
        var otp = await BodyTokensAsync(await VerifyOtpAsync(code.Email));
        await AssertStartedSessionAsync(code.Id, otp.Token, "code");

        // Emailed link.
        var link = await SeedUserAsync();
        var linked = await BodyTokensAsync(await ConfirmLinkAsync(await SeedLinkAsync(link.Email)));
        await AssertStartedSessionAsync(link.Id, linked.Token, "link");

        // Second factor.
        var (mfaUser, secret) = await EnrolledUserAsync();
        await AssertStartedSessionAsync(mfaUser.Id, (await PasswordThenMfaAsync(mfaUser, secret)).Token, "mfa verify");

        // Each social provider path.
        foreach (var provider in new[]
                 {
                     ExternalProviders.Google, ExternalProviders.GitHub, ExternalProviders.Facebook, ExternalProviders.LinkedIn,
                 })
        {
            var social = await SeedUserAsync();
            var tokens = await SocialAsync(social.Email, provider);
            tokens.Allowed.Should().BeTrue(provider);
            await AssertStartedSessionAsync(social.Id, tokens.Token, provider);
        }

        // The OIDC id token grant ends in the same issuer with the configured provider name.
        var oidc = await SeedUserAsync();
        using (var scope = _fixture.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var identity = new OidcIdentity("https://issuer.example", "sub-" + Guid.NewGuid().ToString("N"), oidc.Email, true, null, null);
            var (tokens, _) = await SocialSignIn.IssueForIdentityAsync(
                services.GetRequiredService<IDocumentSession>(),
                services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
                services.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
                services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
                SocialContext(ChromeOnMac),
                identity,
                "native",
                Tenant.DefaultSlug,
                Ct);
            tokens.Allowed.Should().BeTrue();
            await AssertStartedSessionAsync(oidc.Id, tokens.Token, "oidc id token grant");
        }
    }

    [Fact]
    public async Task Session_id_and_start_survive_refresh_and_last_seen_moves()
    {
        var user = await SeedUserAsync();
        var firstIp = NextIp();
        var secondIp = NextIp();
        var login = await LoginAsync(user, ip: firstIp);
        var started = await LiveRowAsync(user.Id);
        started.SessionId.Should().NotBeNullOrEmpty();
        started.SessionStartedAt.Should().NotBeNullOrEmpty();
        started.LastSeenAt.Should().NotBeNullOrEmpty();

        await Task.Delay(1100, Ct);
        var refreshed = await RefreshedAsync(login.Refresh, secondIp);

        Sid(refreshed.Token).Should().Be(started.SessionId);
        var next = await LiveRowAsync(user.Id);
        next.Id.Should().NotBe(started.Id);
        next.SessionId.Should().Be(started.SessionId);
        next.SessionStartedAt.Should().Be(started.SessionStartedAt);
        next.SignInIp.Should().Be(firstIp, "the sign-in address stays what it was when the session started");
        next.LastSeenIp.Should().Be(secondIp);
        DateTime.Parse(next.LastSeenAt!).Should().BeAfter(DateTime.Parse(started.LastSeenAt!));
    }

    [Fact]
    public async Task Used_row_holds_no_ip_or_device()
    {
        var user = await SeedUserAsync();
        var login = await LoginAsync(user);
        var before = await LiveRowAsync(user.Id);
        before.Device.Should().NotBeNullOrEmpty("the row has to carry them first, or their absence below proves nothing");
        before.SignInIp.Should().NotBeNullOrEmpty();
        before.LastSeenIp.Should().NotBeNullOrEmpty();

        await RefreshedAsync(login.Refresh);

        var rows = await RowsAsync(user.Id);
        var used = rows.Single(r => r.Id == before.Id);
        used.IsRevoked.Should().BeTrue();
        used.RevokedReason.Should().Be("used");
        used.Device.Should().BeNullOrEmpty();
        used.SignInIp.Should().BeNullOrEmpty();
        used.LastSeenIp.Should().BeNullOrEmpty();

        var live = rows.Single(r => !r.IsRevoked);
        live.Device.Should().NotBeNullOrEmpty();
        live.SignInIp.Should().NotBeNullOrEmpty();
        live.LastSeenIp.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ended_session_access_token_is_refused_on_next_request()
    {
        var user = await SeedUserAsync();
        var login = await LoginAsync(user);
        var sid = Sid(login.Token);
        sid.Should().NotBeNullOrEmpty();
        (await WhoAmIAsync(login.Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await EndSessionAsync(user.Id, sid!);

        (await WhoAmIAsync(login.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var row = (await RowsAsync(user.Id)).Single();
        row.IsRevoked.Should().BeTrue();
        row.RevokedReason.Should().Be("session_revoked");
        (await RefreshAsync(login.Refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ended_session_is_refused_on_the_realtime_hub()
    {
        await using var host = new SampleProbeHost();
        await host.InitializeAsync();
        var caller = await host.CallerHoldingAsync(["read_probe_tickets"]);
        var sid = Guid.NewGuid().ToString();

        var original = new JwtSecurityTokenHandler().ReadJwtToken(caller.Token);
        var claims = original.Claims.Where(c => c.Type != "sid").Append(new Claim("sid", sid)).ToList();
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: original.Issuer,
            audience: original.Audiences.First(),
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.ASCII.GetBytes(host.JwtKey)), SecurityAlgorithms.HmacSha256)));

        await using var connection = await Realtime.RealtimeHubTests.ConnectAsync(host, token);
        await connection.InvokeAsync("Subscribe", "probe.tickets");

        await using (var session = host.OpenSession())
        {
            session.Store(new RevokedToken
            {
                Id = Guid.NewGuid(),
                TokenJti = $"sid:{sid}",
                UserId = Guid.Parse(original.Claims.First(c => c.Type == "UserId").Value),
                RevokedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                Reason = "session_revoked",
            });
            await session.SaveChangesAsync(Ct);
        }

        var act = () => connection.InvokeAsync("Subscribe", "probe.tickets");
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Ending_one_session_leaves_the_others_signed_in()
    {
        var user = await SeedUserAsync();
        var first = await LoginAsync(user);
        var second = await LoginAsync(user);
        var firstSid = Sid(first.Token);
        var secondSid = Sid(second.Token);
        firstSid.Should().NotBeNullOrEmpty();
        secondSid.Should().NotBeNullOrEmpty().And.NotBe(firstSid, "each sign-in is its own session");

        await EndSessionAsync(user.Id, firstSid!);

        (await WhoAmIAsync(first.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await WhoAmIAsync(second.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(first.Refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var other = (await RowsAsync(user.Id)).Single(r => r.SessionId == secondSid);
        other.IsRevoked.Should().BeFalse();
        (await RefreshAsync(second.Refresh)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_racing_a_session_end_signs_out_only_that_session()
    {
        // The two requests start together, so across rounds the refresh wins sometimes and loses
        // others. Either way the other session must stay signed in, and the loser must not be a
        // reuse detection, which revokes every session the user has.
        for (var round = 0; round < 8; round++)
        {
            var user = await SeedUserAsync();
            var racing = await LoginAsync(user);
            var bystander = await LoginAsync(user);
            var racingSid = Sid(racing.Token);
            racingSid.Should().NotBeNullOrEmpty();

            var refresh = RefreshAsync(racing.Refresh);
            var end = EndSessionAsync(user.Id, racingSid!);
            await Task.WhenAll(refresh, end.ContinueWith(_ => { }, TaskScheduler.Default));

            var status = (await refresh).StatusCode;
            status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);

            var rows = await RowsAsync(user.Id);
            rows.Where(r => r.RevokedReason == "reuse_detected").Should().BeEmpty($"round {round}");
            var other = rows.Single(r => r.SessionId == Sid(bystander.Token));
            other.IsRevoked.Should().BeFalse($"round {round}: the other session is untouched");
            (await WhoAmIAsync(bystander.Token)).StatusCode.Should().Be(HttpStatusCode.OK, $"round {round}");
        }
    }

    [Fact]
    public async Task Tenant_switch_keeps_the_sid()
    {
        var user = await SeedUserAsync();
        var away = $"sess-{Guid.NewGuid():N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = away, Name = away, Status = TenantStatus.Active });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TenantSlug = away,
                Status = MembershipStatus.Active,
                RoleIds = [],
            });
            await session.SaveChangesAsync(Ct);
        }

        var login = await LoginAsync(user);
        var sid = Sid(login.Token);
        sid.Should().NotBeNullOrEmpty();

        var response = await Client(bearer: login.Token).PostAsJsonAsync("/api/me/switch", new { tenant = away }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var switched = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Me.SwitchTenantResponse>(Ct))!.Token;

        Sid(switched).Should().Be(sid, "the switched token belongs to the same session");

        await EndSessionAsync(user.Id, sid!);
        (await WhoAmIAsync(switched, away)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ending the session ends the token it was switched into");
    }

    [Fact]
    public async Task Sign_in_method_records_first_factor_and_mfa()
    {
        var password = await SeedUserAsync();
        await LoginAsync(password);
        (await LiveRowAsync(password.Id)).SignInMethod.Should().Be("password");

        var code = await SeedUserAsync();
        await SeedOtpAsync(code.Email);
        await BodyTokensAsync(await VerifyOtpAsync(code.Email));
        (await LiveRowAsync(code.Id)).SignInMethod.Should().Be("code");

        var link = await SeedUserAsync();
        await BodyTokensAsync(await ConfirmLinkAsync(await SeedLinkAsync(link.Email)));
        (await LiveRowAsync(link.Id)).SignInMethod.Should().Be("link");

        var social = await SeedUserAsync();
        (await SocialAsync(social.Email, ExternalProviders.Google)).Allowed.Should().BeTrue();
        (await LiveRowAsync(social.Id)).SignInMethod.Should().Be("social:google");

        var (passwordMfa, secretA) = await EnrolledUserAsync();
        await PasswordThenMfaAsync(passwordMfa, secretA);
        (await LiveRowAsync(passwordMfa.Id)).SignInMethod.Should().Be("password+mfa");

        var (codeMfa, secretB) = await EnrolledUserAsync();
        await SeedOtpAsync(codeMfa.Email);
        var challenge = (await (await VerifyOtpAsync(codeMfa.Email)).Content
            .ReadFromJsonAsync<barakoCMS.Features.Auth.Otp.OtpVerifyResponse>(Ct))!;
        challenge.RequiresMfa.Should().BeTrue();
        await VerifyMfaAsync(challenge.MfaChallengeToken!, secretB);
        (await LiveRowAsync(codeMfa.Id)).SignInMethod.Should().Be("code+mfa");
    }

    [Fact]
    public async Task Device_text_is_bounded_and_stripped()
    {
        var huge = new string('a', 10 * 1024);

        // 10 KB with nothing a browser or OS name matches.
        var unknown = await SeedUserAsync();
        await LoginAsync(unknown, userAgent: huge);
        (await LiveRowAsync(unknown.Id)).Device.Should().Be("Unknown device");

        // A recognised browser and OS.
        var chrome = await SeedUserAsync();
        await LoginAsync(chrome, userAgent: ChromeOnMac);
        (await LiveRowAsync(chrome.Id)).Device.Should().Be("Chrome on macOS");

        var firefox = await SeedUserAsync();
        await LoginAsync(firefox, userAgent: FirefoxOnLinux);
        (await LiveRowAsync(firefox.Id)).Device.Should().Be("Firefox on Linux");

        // Control characters and markup around a real browser, and a long tail, through a context
        // that does not reject them the way the HTTP client does.
        var hostile = await SeedUserAsync();
        var agent = "\u0001\u0007<script>alert(1)</script>\r\n\t Mozilla/5.0 (Macintosh; Intel Mac OS X) <b>Chrome</b> \u007f" + huge;
        (await SocialAsync(hostile.Email, ExternalProviders.Google, agent)).Allowed.Should().BeTrue();
        var device = (await LiveRowAsync(hostile.Id)).Device!;
        device.Length.Should().BeLessThanOrEqualTo(80);
        device.Any(char.IsControl).Should().BeFalse();
        device.Should().NotContain("<").And.NotContain(">");

        // The client's own device id is capped on write.
        var withId = await SeedUserAsync();
        var client = Client(userAgent: ChromeOnMac);
        client.DefaultRequestHeaders.Add("X-Device-Id", new string('d', 500));
        (await client.PostAsJsonAsync(
            "/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = withId.Username, Password = Password }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LiveRowAsync(withId.Id)).DeviceId.Should().HaveLength(128);
    }

    [Fact]
    public async Task Row_without_session_id_gets_one_on_refresh()
    {
        var user = await SeedUserAsync();
        var plain = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new RefreshToken
            {
                Id = Guid.NewGuid(),
                TokenHash = RefreshToken.HashOf(plain),
                UserId = user.Id,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await session.SaveChangesAsync(Ct);
        }

        var old = (await RowsAsync(user.Id)).Single();
        old.SessionId.Should().BeNullOrEmpty("this is a row from before the change");

        var refreshed = await RefreshedAsync(plain);

        var sid = Sid(refreshed.Token);
        sid.Should().NotBeNullOrEmpty();
        var live = await LiveRowAsync(user.Id);
        live.SessionId.Should().Be(sid);
        live.SessionStartedAt.Should().NotBeNullOrEmpty();

        var again = await RefreshedAsync(refreshed.Refresh);
        Sid(again.Token).Should().Be(sid, "from then on the id is carried like any other");
    }

    [Fact]
    public async Task Erase_blanks_ip_and_device_on_every_row()
    {
        var user = await SeedUserAsync();
        var login = await LoginAsync(user);
        await RefreshedAsync(login.Refresh);
        await LoginAsync(user);

        var rows = await RowsAsync(user.Id);
        rows.Should().HaveCount(3);
        rows.Where(r => !r.IsRevoked).Should().OnlyContain(r =>
            !string.IsNullOrEmpty(r.Device) && !string.IsNullOrEmpty(r.SignInIp) && !string.IsNullOrEmpty(r.LastSeenIp),
            "the live rows have to hold them first, or their absence below proves nothing");

        // A used row from before rotation cleared them still holds them.
        var used = rows.Single(r => r.IsRevoked);
        var camel = used.Text("tokenHash") is not null;
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            var patch = camel
                ? """{"device":"Chrome on macOS","signInIp":"203.0.113.5","lastSeenIp":"203.0.113.6"}"""
                : """{"Device":"Chrome on macOS","SignInIp":"203.0.113.5","LastSeenIp":"203.0.113.6"}""";
            await using var command = new NpgsqlCommand(
                "update public.mt_doc_refresh_tokens set data = data || @patch::jsonb where id = @id", connection);
            command.Parameters.AddWithValue("patch", patch);
            command.Parameters.AddWithValue("id", used.Id);
            (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var current = (await session.LoadAsync<User>(user.Id, Ct))!;
            await scope.ServiceProvider.GetRequiredService<UserEraser>().QueueEraseAsync(session, current, "test", Ct);
            await session.SaveChangesAsync(Ct);
        }

        var after = await RowsAsync(user.Id);
        after.Should().HaveCount(3, "the rows stay, only what identifies the person goes");
        after.Should().OnlyContain(r =>
            string.IsNullOrEmpty(r.Device) && string.IsNullOrEmpty(r.SignInIp) && string.IsNullOrEmpty(r.LastSeenIp));
    }

    [Fact]
    public async Task Logout_still_leaves_other_access_tokens_until_expiry()
    {
        var user = await SeedUserAsync();
        var first = await LoginAsync(user);
        var second = await LoginAsync(user);

        var logout = await Client(bearer: first.Token).PostAsJsonAsync("/api/auth/logout", new { refreshToken = first.Refresh }, Ct);
        logout.IsSuccessStatusCode.Should().BeTrue();

        (await WhoAmIAsync(first.Token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await WhoAmIAsync(second.Token)).StatusCode.Should().Be(HttpStatusCode.OK,
            "logout ends the token it was sent with and the refresh token, not the user's other sessions");
    }
}
