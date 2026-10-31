using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>Routes the OIDC stub's host to the OIDC stub and everything else to the OAuth stub.</summary>
internal sealed class HandoffRoutingHandler(HttpMessageHandler oauth, HttpMessageHandler oidc) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var target = request.RequestUri!.Host == "idp.test.example" ? oidc : oauth;
        return new HttpMessageInvoker(target, disposeHandler: false).SendAsync(request, ct);
    }
}

/// <summary>
/// Facebook asks for email and public_profile only (#1270), and the code handoff for server-side
/// frontends: a callback redirects with a single-use code, and the exchange returns the tokens.
/// </summary>
/// <remarks>
/// Over real HTTP with the provider answered by stubs. Three hosts share the database: one with the
/// default settings, one with <c>ExternalAuth:CodeHandoff</c> on, and one with the Facebook opt-in.
/// Cookies are attached by hand for the reason given on <see cref="ExternalAuthCallbackTests"/>.
/// Each refusal sits beside the same request succeeding, so an endpoint that always refused would
/// fail the pair.
/// </remarks>
[Collection("Integration1")]
public class SocialHandoffTests
{
    private const string BaseUrl = "https://cms.test.example";
    private const string Device = "device-cookie-value-1";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _default;
    private static WebApplicationFactory<Program>? _handoff;
    private static WebApplicationFactory<Program>? _extended;
    private static OAuthStubHandler? _oauth;
    private static OidcStubProvider? _oidc;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public SocialHandoffTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            if (_default is null)
            {
                _oauth = new OAuthStubHandler();
                _oidc = new OidcStubProvider();
                var clients = new SingleClientFactory(new HandoffRoutingHandler(_oauth, _oidc));
                WebApplicationFactory<Program> Host(Dictionary<string, string?> extra) => fixture.WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings(extra)));
                    b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(clients));
                });
                _default = Host([]);
                _handoff = Host(new() { { "ExternalAuth:CodeHandoff", "true" } });
                _extended = Host(new() { { "Facebook:RequestBirthdayAndLocation", "true" } });
            }
        }
    }

    private static Dictionary<string, string?> Settings(Dictionary<string, string?> extra)
    {
        var all = new Dictionary<string, string?>
        {
            { "App:BaseUrl", BaseUrl },
            { "Google:ClientId", "google-client" },
            { "Google:ClientSecret", "google-secret" },
            { "GitHub:ClientId", "github-client" },
            { "GitHub:ClientSecret", "github-secret" },
            { "Facebook:AppId", "fb-app" },
            { "Facebook:AppSecret", "fb-secret" },
            { "Facebook:TrustUnverifiedEmail", "true" },
            { "LinkedIn:ClientId", "" },
            { "Oidc:Providers:stub:Authority", OidcStubProvider.Authority },
            { "Oidc:Providers:stub:ClientId", OidcStubProvider.ClientId },
            { "Oidc:Providers:stub:ClientSecret", OidcStubProvider.ClientSecret },
        };
        foreach (var (key, value) in extra) all[key] = value;
        return all;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.101.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private static string Prefix(string provider) => provider switch { "google" => "gg", "facebook" => "fb", _ => "gh" };

    /// <summary>Points the shared stub at a fresh account for the provider and returns its email.</summary>
    private static string NewAccount(string provider, string? email = null)
    {
        email ??= $"{Unique("h")}@example.com";
        switch (provider)
        {
            case "google":
                _oauth!.GoogleSub = Unique("gsub");
                _oauth.GoogleDomain = null;
                _oauth.GoogleEmail = email;
                break;
            case "facebook":
                _oauth!.FacebookId = Unique("fb");
                _oauth.FacebookEmail = email;
                break;
            default:
                _oauth!.GitHubId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
                _oauth.GitHubEmail = email;
                break;
        }

        return email;
    }

    private static Task<HttpResponseMessage> CallbackAsync(
        WebApplicationFactory<Program> host, string provider, string? device = Device)
    {
        var prefix = Prefix(provider);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/{provider}/callback?code=good&state=s1");
        request.Headers.Add("Cookie", $"{prefix}_state=s1; {prefix}_club=" + (device is null ? "" : $"; social_device={device}"));
        return Client(host).SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> ExchangeAsync(string? code, string? device = Device, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/social/exchange")
        {
            Content = JsonContent.Create(new { code }),
        };
        if (device is not null) request.Headers.Add("Cookie", $"social_device={device}");
        return (client ?? Client(_handoff!)).SendAsync(request, Ct);
    }

    private static string CodeIn(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/auth/social?code=");
        return System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]!;
    }

    private async Task<string> SignedInCodeAsync(string provider = "google", string? email = null)
    {
        NewAccount(provider, email);
        return CodeIn(await CallbackAsync(_handoff!, provider));
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    // ---- Facebook scope ----

    private static async Task<string> FacebookScopeAsync(WebApplicationFactory<Program> host)
    {
        var response = await Client(host).GetAsync("/api/auth/facebook/start", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.Host.Should().Be("www.facebook.com");
        return System.Web.HttpUtility.ParseQueryString(location.Query)["scope"]!;
    }

    [Fact]
    public async Task A_facebook_start_asks_for_email_and_public_profile_only()
    {
        (await FacebookScopeAsync(_default!)).Should().Be("email,public_profile");
    }

    [Fact]
    public async Task A_facebook_start_asks_for_birthday_and_location_only_when_the_setting_is_on()
    {
        (await FacebookScopeAsync(_extended!)).Should().Be("email,public_profile,user_birthday,user_location");
        (await FacebookScopeAsync(_default!)).Should().NotContain("birthday").And.NotContain("location");
    }

    // ---- the default redirect ----

    [Theory]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("facebook")]
    public async Task Without_the_handoff_a_callback_still_redirects_with_the_tokens_in_the_fragment(string provider)
    {
        NewAccount(provider);

        var response = await CallbackAsync(_default!, provider, device: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/auth/social#token=");
        location.Should().Contain("&refresh=").And.Contain("&club=");
        location.Should().NotContain("code=");
    }

    [Fact]
    public async Task Without_the_handoff_a_start_route_sets_no_device_cookie()
    {
        var response = await Client(_default!).GetAsync("/api/auth/google/start", Ct);

        response.Headers.GetValues("Set-Cookie").Should().NotContain(c => c.StartsWith("social_device=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_handoff_a_start_route_sets_the_device_cookie()
    {
        foreach (var provider in new[] { "google", "github", "facebook" })
        {
            var response = await Client(_handoff!).GetAsync($"/api/auth/{provider}/start", Ct);

            var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("social_device=", StringComparison.Ordinal));
            cookie.Should().Contain("httponly").And.Contain("secure");
            cookie.Split(';')[0]["social_device=".Length..].Length.Should().BeGreaterThan(30);
        }
    }

    // ---- the handoff redirect ----

    [Theory]
    [InlineData("google")]
    [InlineData("github")]
    [InlineData("facebook")]
    public async Task With_the_handoff_on_a_callback_redirect_carries_only_the_code(string provider)
    {
        NewAccount(provider);

        var response = await CallbackAsync(_handoff!, provider);

        var location = response.Headers.Location!.ToString();
        var code = CodeIn(response);
        code.Length.Should().BeGreaterThan(30);
        location.Should().Be($"{BaseUrl}/auth/social?code={Uri.EscapeDataString(code)}", "nothing but the code travels");
        location.Should().NotContain("#").And.NotContain("token").And.NotContain("refresh").And.NotContain("mfa_challenge");
    }

    [Fact]
    public async Task With_the_handoff_on_a_callback_without_the_device_cookie_goes_back_to_the_login_page()
    {
        var email = NewAccount("google");
        var user = new User { Id = Guid.NewGuid(), Email = email, Username = Unique("rt"), PasswordHash = "x" };
        await StoreAsync(user);

        var response = await CallbackAsync(_handoff!, "google", device: null);

        response.Headers.Location!.ToString().Should().StartWith($"{BaseUrl}/login?fberror=");
        response.Headers.Location!.ToString().Should().NotContain("code=");
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<RefreshToken>()
            .CountAsync(t => t.UserId == user.Id, Ct)).Should().Be(0, "the refusal came before any token was issued");
    }

    [Fact]
    public async Task With_the_handoff_on_an_oidc_callback_carries_only_the_code()
    {
        var host = Client(_handoff!);
        var start = await host.GetAsync("/api/auth/oidc/stub/start", Ct);
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var cookies = start.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToList();
        var flow = cookies.Single(c => c.StartsWith("__Host-oidc_stub=", StringComparison.Ordinal));
        var deviceCookie = cookies.Single(c => c.StartsWith("social_device=", StringComparison.Ordinal));
        var sent = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query);

        var email = $"{Unique("o")}@example.com";
        var claims = OidcTestTokens.Claims(sent["nonce"]!, Unique("osub"), email);
        var code = "code-" + Guid.NewGuid().ToString("N");
        _oidc!.Codes[code] = (OidcTestTokens.Rs256(_oidc.Key, claims), sent["code_challenge"]);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/oidc/stub/callback?code={code}&state={sent["state"]}");
        request.Headers.Add("Cookie", $"{flow}; __Host-oidc_stub_club=; {deviceCookie}");
        var response = await host.SendAsync(request, Ct);

        var handoff = CodeIn(response);
        response.Headers.Location!.ToString().Should().Be($"{BaseUrl}/auth/social?code={Uri.EscapeDataString(handoff)}");
        (await ExchangeAsync(handoff, deviceCookie["social_device=".Length..])).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- the exchange ----

    [Fact]
    public async Task A_code_exchanges_once_with_200_and_a_second_use_answers_400()
    {
        var email = $"{Unique("x")}@example.com";
        var code = await SignedInCodeAsync("google", email);

        var first = await ExchangeAsync(code);
        var second = await ExchangeAsync(code);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("requiresMfa").GetBoolean().Should().BeFalse();
        body.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("token").GetString());
        using var scope = _fixture.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<User>().FirstAsync(u => u.Email == email, Ct);
        jwt.Claims.Should().Contain(c => c.Type == "UserId" && c.Value == user.Id.ToString(), "the token is for who signed in");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_expired_code_answers_400()
    {
        var outcome = new SocialHandoffOutcome("jwt", "refresh", false, null, "");
        var expired = $"expired-{Guid.NewGuid():N}";
        var live = $"live-{Guid.NewGuid():N}";
        await StoreAsync(
            SocialHandoff.Build(expired, Device, outcome, DateTime.UtcNow.AddSeconds(-1)),
            SocialHandoff.Build(live, Device, outcome, DateTime.UtcNow.AddSeconds(30)));

        (await ExchangeAsync(expired)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ExchangeAsync(live)).StatusCode.Should().Be(HttpStatusCode.OK, "the same row a moment earlier from its expiry would have worked");
    }

    [Fact]
    public async Task A_code_from_another_device_answers_400_and_is_not_used_up()
    {
        var code = await SignedInCodeAsync();

        (await ExchangeAsync(code, device: "someone-elses-device")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ExchangeAsync(code, device: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no cookie is another device");
        (await ExchangeAsync(code)).StatusCode.Should().Be(HttpStatusCode.OK, "the right device still gets it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-code")]
    public async Task A_missing_or_unknown_code_answers_400(string? code)
    {
        (await ExchangeAsync(code)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_oversized_code_answers_400()
    {
        (await ExchangeAsync(new string('a', 5000))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_exchanges_racing_for_one_code_let_exactly_one_win()
    {
        var code = await SignedInCodeAsync();
        var client = Client(_handoff!);
        client.DefaultRequestHeaders.Remove(TestRemoteIpFilter.Header);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            (await ExchangeAsync(code, client: Client(_handoff!))).StatusCode));

        statuses.Should().HaveCount(8);
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1, "a code works once");
        statuses.Count(s => s == HttpStatusCode.BadRequest).Should().Be(7);
    }

    [Fact]
    public async Task An_account_with_mfa_exchanges_the_code_for_the_challenge_and_no_tokens()
    {
        var user = new User { Id = Guid.NewGuid(), Email = $"{Unique("m")}@example.com", Username = Unique("m"), PasswordHash = "" };
        using (var scope = _fixture.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector>();
            await StoreAsync(user, new MfaSecret
            {
                Id = user.Id,
                EncryptedSecret = protector.Protect(OtpNet.Base32Encoding.ToString(OtpNet.KeyGeneration.GenerateRandomKey(20))),
                Enabled = true,
                ConfirmedAt = DateTime.UtcNow,
            });
        }

        var code = await SignedInCodeAsync("google", user.Email);
        var response = await ExchangeAsync(code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("requiresMfa").GetBoolean().Should().BeTrue();
        body.GetProperty("mfaChallengeToken").GetString().Should().NotBeNullOrEmpty();
        body.TryGetProperty("token", out var token).Should().BeTrue();
        token.ValueKind.Should().Be(JsonValueKind.Null, "the second factor is still owed");
    }

    [Fact]
    public async Task The_stored_code_is_hashed_and_the_tokens_are_not_readable_from_the_row()
    {
        var code = await SignedInCodeAsync();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            (await session.LoadAsync<SocialHandoffCode>(code, Ct)).Should().BeNull("the code itself is never the key");
            var row = await session.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(code), Ct);
            row.Should().NotBeNull();
            row!.Payload.Should().NotBeNullOrEmpty();
            row.Payload.Should().NotContain("eyJ", "a JWT is not stored in the clear");
            row.DeviceHash.Should().NotBe(Device).And.Be(SocialHandoff.HashOf(Device));
            row.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(10));
        }

        (await ExchangeAsync(code)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = _fixture.Services.CreateScope())
        {
            var used = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(code), Ct);
            used!.Used.Should().BeTrue();
            used.Payload.Should().BeEmpty("a spent code keeps no tokens");
        }
    }

    // ---- bounds ----

    [Fact]
    public async Task Purging_deletes_expired_codes_and_keeps_live_ones()
    {
        var outcome = new SocialHandoffOutcome("jwt", "refresh", false, null, "");
        var expired = Enumerable.Range(0, 3).Select(_ => $"old-{Guid.NewGuid():N}").ToList();
        var live = $"live-{Guid.NewGuid():N}";
        await StoreAsync([
            .. expired.Select(c => (object)SocialHandoff.Build(c, Device, outcome, DateTime.UtcNow.AddMinutes(-5))),
            SocialHandoff.Build(live, Device, outcome, DateTime.UtcNow.AddSeconds(50)),
        ]);

        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var removed = await SocialHandoff.PurgeAsync(store, Ct);

        removed.Should().BeGreaterThanOrEqualTo(3);
        await using var query = store.QuerySession();
        foreach (var id in expired)
        {
            (await query.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(id), Ct)).Should().BeNull();
        }

        (await query.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(live), Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Purging_when_due_runs_once_per_interval()
    {
        var outcome = new SocialHandoffOutcome("jwt", "refresh", false, null, "");
        var first = $"old-{Guid.NewGuid():N}";
        var second = $"old-{Guid.NewGuid():N}";
        await StoreAsync([SocialHandoff.Build(first, Device, outcome, DateTime.UtcNow.AddMinutes(-5))]);
        SocialHandoff.ResetPurgeClock();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await SocialHandoff.PurgeIfDueAsync(store, scope.ServiceProvider, Ct);

        await using (var query = store.QuerySession())
        {
            (await query.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(first), Ct)).Should().BeNull("the first call was due");
        }

        await StoreAsync([SocialHandoff.Build(second, Device, outcome, DateTime.UtcNow.AddMinutes(-5))]);
        await SocialHandoff.PurgeIfDueAsync(store, scope.ServiceProvider, Ct);

        await using var again = store.QuerySession();
        (await again.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(second), Ct)).Should().NotBeNull("the interval has not passed");
        SocialHandoff.ResetPurgeClock();
        await SocialHandoff.PurgeIfDueAsync(store, scope.ServiceProvider, Ct);
        await using var last = store.QuerySession();
        (await last.LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(second), Ct)).Should().BeNull("due again after a reset");
    }

    [Fact]
    public async Task Live_codes_are_capped()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var now = DateTime.UtcNow;
        var before = await session.Query<SocialHandoffCode>().CountAsync(c => c.ExpiresAt > now && !c.Used, Ct);

        (await SocialHandoff.HasRoomAsync(session, before + 1, Ct)).Should().BeTrue("one more fits");
        (await SocialHandoff.HasRoomAsync(session, before, Ct)).Should().BeFalse("at the cap, no more");
        (await SocialHandoff.HasRoomAsync(session, SocialHandoff.MaxLive, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_callback_purges_expired_codes_when_the_purge_is_due()
    {
        var code = $"old-{Guid.NewGuid():N}";
        var outcome = new SocialHandoffOutcome("t", "r", false, null, "");
        await StoreAsync([SocialHandoff.Build(code, Device, outcome, DateTime.UtcNow.AddMinutes(-5))]);
        SocialHandoff.ResetPurgeClock();
        NewAccount("google");

        var response = await CallbackAsync(_handoff!, "google");

        CodeIn(response);
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<SocialHandoffCode>(SocialHandoff.HashOf(code), Ct)).Should().BeNull("the callback ran the purge");
    }

    [Fact]
    public async Task A_callback_at_the_live_code_cap_goes_back_to_the_login_page_and_issues_no_token()
    {
        var email = NewAccount("google");
        var user = new User { Id = Guid.NewGuid(), Email = email, Username = Unique("busy"), PasswordHash = "x" };
        await StoreAsync(user);
        var cap = SocialHandoff.MaxLive;
        SocialHandoff.MaxLive = 0;
        try
        {
            var response = await CallbackAsync(_handoff!, "google");

            var location = response.Headers.Location!.ToString();
            location.Should().StartWith($"{BaseUrl}/login?fberror=");
            Uri.UnescapeDataString(location).Should().Contain("Sign-in is busy");
            location.Should().NotContain("code=");
        }
        finally
        {
            SocialHandoff.MaxLive = cap;
        }

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<RefreshToken>()
            .CountAsync(t => t.UserId == user.Id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Starting_a_sign_in_stores_nothing()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var before = await session.Query<SocialHandoffCode>().CountAsync(Ct);

        for (var i = 0; i < 20; i++)
        {
            (await Client(_handoff!).GetAsync("/api/auth/google/start", Ct)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        (await session.Query<SocialHandoffCode>().CountAsync(Ct)).Should().Be(before, "a start only sets a cookie");
    }

    // ---- a sign-in during a deletion window ----

    private const string FacebookSecret = "fb-secret";

    private async Task<string> RequestFacebookDeletionAsync(string facebookId)
    {
        var response = await Client(_default!).PostAsync("/api/auth/facebook/data-deletion", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("signed_request", SignedRequests.For(facebookId, secret: FacebookSecret))]), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsyncElement()).GetProperty("confirmation_code").GetString()!;
    }

    private async Task<FacebookDeletionRequest> DeletionRequestAsync(string code)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<FacebookDeletionRequest>(FacebookDeletionFlow.HashOf(code), Ct))!;
    }

    [Fact]
    public async Task A_facebook_sign_in_in_the_window_keeps_the_account_even_though_no_link_is_stored()
    {
        // Facebook:TrustUnverifiedEmail is on for this host, so the returning person matches by
        // address and no link row is written. The sweep would otherwise find no other login.
        var facebookId = Unique("fb");
        var user = new User { Id = Guid.NewGuid(), Email = $"{Unique("w")}@example.com", Username = Unique("w"), PasswordHash = "" };
        await StoreAsync(user, new ExternalIdentity
        {
            Id = ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookId), Issuer = ExternalProviders.Facebook,
            Subject = facebookId, UserId = user.Id, Provider = ExternalProviders.Facebook,
        });
        var code = await RequestFacebookDeletionAsync(facebookId);
        (await DeletionRequestAsync(code)).Status.Should().Be(FacebookDeletionStatus.AwaitingChoice);

        // The window runs out just before the person signs in, so only the sign-in saves them.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var row = await session.LoadAsync<FacebookDeletionRequest>(FacebookDeletionFlow.HashOf(code), Ct);
            row!.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            session.Store(row);
            await session.SaveChangesAsync(Ct);
        }

        _oauth!.FacebookId = facebookId;
        _oauth.FacebookEmail = user.Email;
        var response = await CallbackAsync(_default!, "facebook", device: null);

        response.Headers.Location!.ToString().Should().StartWith($"{BaseUrl}/auth/social#token=");
        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                .LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookId), Ct))
                .Should().BeNull("this is the case where the sign-in stores no link");
        }

        var settled = await DeletionRequestAsync(code);
        settled.Status.Should().Be(FacebookDeletionStatus.Kept);
        settled.UserId.Should().BeNull();
        settled.ExpiresAt.Should().BeNull();
        settled.SettledAt.Should().NotBeNull();

        await FacebookDeletionFlow.SweepAsync(_fixture.Services, DateTime.UtcNow, Ct);

        using var check = _fixture.Services.CreateScope();
        var kept = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct);
        kept!.Username.Should().Be(user.Username, "the account is still there");
        (await DeletionRequestAsync(code)).Status.Should().Be(FacebookDeletionStatus.Kept);
    }

    // ---- the profile a deletion request clears ----

    private async Task<SocialProfile?> ProfileOfAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<SocialProfile>().FirstOrDefaultAsync(p => p.UserId == userId, Ct);
    }

    /// <summary>An existing account with a password, so a trusted-address sign-in stores no link.</summary>
    private async Task<User> ExistingUserAsync()
    {
        var user = new User { Id = Guid.NewGuid(), Email = $"{Unique("p")}@example.com", Username = Unique("p"), PasswordHash = "x" };
        await StoreAsync(user);
        return user;
    }

    private async Task SignInAsync(string provider, string email, string? facebookId = null)
    {
        NewAccount(provider, email);
        if (facebookId is not null) _oauth!.FacebookId = facebookId;
        (await CallbackAsync(_default!, provider, device: null)).Headers.Location!.ToString()
            .Should().StartWith($"{BaseUrl}/auth/social#token=");
    }

    [Fact]
    public async Task Facebook_then_google_a_deletion_request_clears_facebooks_fields_and_keeps_googles()
    {
        var user = await ExistingUserAsync();
        var facebookId = Unique("fb");
        _oauth!.FacebookBirthday = "01/02/1990";
        _oauth.FacebookLocation = "Manila";
        try
        {
            await SignInAsync("facebook", user.Email, facebookId);
            var afterFacebook = await ProfileOfAsync(user.Id);
            afterFacebook!.Birthday.Should().Be("01/02/1990");
            afterFacebook.FacebookKey.Should().Be(ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookId))
                .And.NotContain(facebookId, "the raw Facebook id is not stored");

            await SignInAsync("google", user.Email);
            (await ProfileOfAsync(user.Id))!.Source.Should().Be("google");

            await RequestFacebookDeletionAsync(facebookId);

            var after = await ProfileOfAsync(user.Id);
            after.Should().NotBeNull("Google's name is not Facebook's to remove");
            after!.Name.Should().Be("Gee Person");
            after.Birthday.Should().BeNull();
            after.Location.Should().BeNull();
        }
        finally
        {
            _oauth.FacebookBirthday = null;
            _oauth.FacebookLocation = null;
        }
    }

    [Fact]
    public async Task Google_then_facebook_a_deletion_request_keeps_the_name_google_supplied()
    {
        var user = await ExistingUserAsync();
        var facebookId = Unique("fb");
        _oauth!.FacebookBirthday = "01/02/1990";
        _oauth.FacebookName = null;
        try
        {
            await SignInAsync("google", user.Email);
            await SignInAsync("facebook", user.Email, facebookId);
            (await ProfileOfAsync(user.Id))!.Birthday.Should().Be("01/02/1990");

            await RequestFacebookDeletionAsync(facebookId);

            var after = await ProfileOfAsync(user.Id);
            after.Should().NotBeNull("the row also holds what Google supplied");
            after!.Name.Should().Be("Gee Person");
            after.Birthday.Should().BeNull();
        }
        finally
        {
            _oauth.FacebookBirthday = null;
            _oauth.FacebookName = "Eff Bee";
        }
    }

    [Fact]
    public async Task A_facebook_profile_is_removed_when_the_sign_in_stored_no_link()
    {
        var user = await ExistingUserAsync();
        var facebookId = Unique("fb");
        _oauth!.FacebookBirthday = "01/02/1990";
        try
        {
            await SignInAsync("facebook", user.Email, facebookId);
            using (var scope = _fixture.Services.CreateScope())
            {
                (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
                    .LoadAsync<ExternalIdentity>(ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookId), Ct))
                    .Should().BeNull("a trusted address links nothing");
            }

            var code = await RequestFacebookDeletionAsync(facebookId);

            (await ProfileOfAsync(user.Id)).Should().BeNull("Facebook filled all of it");
            (await DeletionRequestAsync(code)).Status.Should().Be(FacebookDeletionStatus.Completed, "no window opens without a link");
            using var check = _fixture.Services.CreateScope();
            (await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct))!
                .Username.Should().Be(user.Username);
        }
        finally
        {
            _oauth.FacebookBirthday = null;
        }
    }
}
