using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// Sign-in through a provider configured under <c>Oidc:Providers</c>, over real HTTP against a stub
/// identity provider (#786).
/// </summary>
/// <remarks>
/// Both endpoints are anonymous and the callback hands out a session, so what they refuse is the
/// security of the flow. Every refusal below is paired with a sign-in that succeeds on the same
/// host, because a callback that refused everything would pass each refusal on its own.
///
/// One host serves the whole class. Each test takes its own client address, so each has its own
/// rate limit bucket, and its own authorization codes, so the stub's answers do not cross.
///
/// Clients are built with <c>HandleCookies = false</c> and the cookies are attached by hand, for the
/// reason given on <see cref="ExternalAuthCallbackTests"/>: the cookies are <c>Secure</c> and the
/// test host speaks plain HTTP.
/// </remarks>
[Collection("Sequential")]
public class OidcSignInTests
{
    private const string BaseUrl = "https://cms.test.example";
    private const string Callback = BaseUrl + "/api/auth/oidc/stub/callback";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static OidcStubProvider? _stub;
    private static RecordingClientFactory? _clients;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public OidcSignInTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        lock (Gate)
        {
            if (_host is null)
            {
                var stub = new OidcStubProvider();
                var clients = new RecordingClientFactory(stub);
                _host = fixture.WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings()));
                    b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(clients));
                });
                _stub = stub;
                _clients = clients;
            }
        }

        _client = ClientOf(_host);
    }

    private static OidcStubProvider Stub => _stub!;

    private static Dictionary<string, string?> Settings() => new()
    {
        { "App:BaseUrl", BaseUrl },
        { "Oidc:Providers:stub:Authority", OidcStubProvider.Authority },
        { "Oidc:Providers:stub:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:stub:ClientSecret", OidcStubProvider.ClientSecret },
        { "Oidc:Providers:stub:DisplayName", "Stub ID" },
        { "Oidc:Providers:entra:Authority", OidcStubProvider.SharedAuthority },
        { "Oidc:Providers:entra:Issuer", OidcStubProvider.SharedIssuerTemplate },
        { "Oidc:Providers:entra:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:entra:ClientSecret", OidcStubProvider.ClientSecret },
        { "Oidc:Providers:entra:EmailVerifiedClaim", "xms_edov" },
        { "Oidc:Providers:plain:Authority", "http://idp.test.example" },
        { "Oidc:Providers:plain:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:plain:ClientSecret", OidcStubProvider.ClientSecret },
        { "Oidc:Providers:dark:Authority", OidcStubProvider.Authority },
        { "Oidc:Providers:dark:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:dark:ClientSecret", OidcStubProvider.ClientSecret },
        { "Oidc:Providers:dark:Enabled", "false" },
        { "Oidc:Providers:broken:Authority", "https://broken.test.example" },
        { "Oidc:Providers:broken:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:broken:ClientSecret", OidcStubProvider.ClientSecret },
        { "GitHub:ClientId", "" },
        { "Google:ClientId", "" },
        { "Facebook:AppId", "" },
        { "LinkedIn:ClientId", "" },
    };

    private static HttpClient ClientOf(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        var n = Interlocked.Increment(ref _ipCounter);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{n % 250 + 1}");
        return client;
    }

    private sealed record Started(HttpResponseMessage Response, Uri Location, string State, string Nonce, string Challenge, string Cookie);

    private async Task<Started> StartAsync(string provider = "stub", string query = "")
    {
        var response = await _client.GetAsync($"/api/auth/oidc/{provider}/start{query}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        var sent = System.Web.HttpUtility.ParseQueryString(location.Query);
        var prefix = $"__Host-oidc_{provider}=";
        var cookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return new Started(response, location, sent["state"]!, sent["nonce"]!, sent["code_challenge"]!, cookie.Split(';')[0][prefix.Length..]);
    }

    private async Task<HttpResponseMessage> CallbackAsync(
        string code, string state, string? cookie, string provider = "stub", string club = "", string extraQuery = "")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/auth/oidc/{provider}/callback?code={code}&state={state}{extraQuery}");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"__Host-oidc_{provider}={cookie}; __Host-oidc_{provider}_club={club}");
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Start, have the stub issue a code for the token the test builds, and come back with it.</summary>
    private async Task<HttpResponseMessage> SignInAsync(
        Func<Started, string> idToken, string provider = "stub", string club = "")
    {
        var started = await StartAsync(provider, club.Length > 0 ? $"?club={club}" : "");
        var code = NewCode();
        Stub.Codes[code] = (idToken(started), started.Challenge);
        return await CallbackAsync(code, started.State, started.Cookie, provider, club);
    }

    private static bool IsProviderUrl(string url) =>
        url.StartsWith("https://idp.test.example/", StringComparison.Ordinal)
        || url.StartsWith("https://broken.test.example/", StringComparison.Ordinal);

    /// <summary>Requests the sign-in flow sent out so far, whatever else on the host made calls.</summary>
    private static int ProviderRequests() => _clients!.Requests.Count(r => IsProviderUrl(r.Url));

    private static string NewCode() => "code-" + Guid.NewGuid().ToString("N");

    private static string NewSubject() => "sub-" + Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"oidc-{Guid.NewGuid():n}@example.com";

    private string Token(Started started, string subject, string email, Action<Dictionary<string, object?>>? edit = null)
    {
        var claims = OidcTestTokens.Claims(started.Nonce, subject, email);
        edit?.Invoke(claims);
        return OidcTestTokens.Rs256(Stub.Key, claims);
    }

    private static void ShouldBeSignedIn(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/auth/social#token=");
        location.Should().Contain("&refresh=");
    }

    private static void ShouldBeRefused(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/login?fberror=", because);
        location.Should().NotContain("token=", because);
        location.Should().NotContain("mfa_challenge=", because);
    }

    private static string AccessTokenFrom(HttpResponseMessage response)
    {
        var fragment = response.Headers.Location!.ToString().Split('#')[1];
        return System.Web.HttpUtility.ParseQueryString(fragment)["token"]!;
    }

    private async Task<barakoCMS.Models.User?> UserByEmailAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<barakoCMS.Models.User>()
            .FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken);
    }

    private async Task<ExternalIdentity?> LinkAsync(string issuer, string subject)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<ExternalIdentity>(
            ExternalIdentity.KeyOf(issuer, subject), TestContext.Current.CancellationToken);
    }

    private async Task<barakoCMS.Models.User> CreateUserAsync(string email, bool mfaEnrolled = false)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Username = $"oidc-{Guid.NewGuid():n}",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
        };
        session.Store(user);
        if (mfaEnrolled)
        {
            var protector = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaSecretProtector>();
            session.Store(new barakoCMS.Models.MfaSecret
            {
                Id = user.Id,
                EncryptedSecret = protector.Protect(OtpNet.Base32Encoding.ToString(OtpNet.KeyGeneration.GenerateRandomKey(20))),
                Enabled = true,
                ConfirmedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    // ---- start ----

    [Fact]
    public async Task A_start_sends_the_browser_to_the_discovered_endpoint_with_state_nonce_and_a_pkce_challenge()
    {
        var started = await StartAsync();

        started.Location.GetLeftPart(UriPartial.Path).Should().Be(OidcStubProvider.Authority + "/authorize",
            "the authorization endpoint comes from the issuer's discovery document");
        var sent = System.Web.HttpUtility.ParseQueryString(started.Location.Query);
        sent["response_type"].Should().Be("code");
        sent["client_id"].Should().Be(OidcStubProvider.ClientId);
        sent["scope"].Should().Be("openid email profile");
        sent["redirect_uri"].Should().Be(Callback);
        sent["code_challenge_method"].Should().Be("S256");
        started.State.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        started.Nonce.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        started.Nonce.Should().NotBe(started.State);

        var parts = started.Cookie.Split('.');
        parts.Should().HaveCount(3);
        parts[0].Should().Be(started.State, "the callback compares the returned state with this");
        parts[1].Should().Be(started.Nonce, "the callback compares the id token's nonce with this");
        OidcTestTokens.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(parts[2]))).Should().Be(started.Challenge,
            "the provider is sent the SHA-256 of the verifier and never the verifier");
        started.Location.Query.Should().NotContain(parts[2]);

        var setCookie = started.Response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("__Host-oidc_stub=", StringComparison.Ordinal)).ToLowerInvariant();
        setCookie.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax").And.Contain("path=/");
        setCookie.Should().NotContain("domain=", "a __Host- cookie names no domain, or the browser refuses it");
    }

    [Fact]
    public async Task Nothing_in_the_request_changes_where_the_browser_is_sent_back_to()
    {
        var evil = Uri.EscapeDataString("https://evil.example/steal");
        var started = await StartAsync(query: $"?redirect_uri={evil}&returnUrl={evil}&redirect={evil}");

        System.Web.HttpUtility.ParseQueryString(started.Location.Query)["redirect_uri"].Should().Be(Callback);
        started.Location.ToString().Should().NotContain("evil.example");

        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), started.Challenge);
        var response = await CallbackAsync(code, started.State, started.Cookie,
            extraQuery: $"&redirect_uri={evil}&returnUrl={evil}&redirect={evil}");

        ShouldBeSignedIn(response);
        response.Headers.Location!.ToString().Should().NotContain("evil.example",
            "after sign-in the browser goes to the configured base URL and nowhere a caller named");
        Stub.TokenRequests[code].Form["redirect_uri"].Should().Be(Callback,
            "the exchange names the configured callback too, or the provider would refuse it");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("dark")]
    [InlineData("nobody")]
    [InlineData("bad%20name")]
    public async Task A_provider_that_is_not_on_cannot_be_started_or_called_back(string provider)
    {
        var before = ProviderRequests();

        (await _client.GetAsync($"/api/auth/oidc/{provider}/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/auth/oidc/{provider}/callback?code=c&state=s", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        ProviderRequests().Should().Be(before,
            "an http authority, a disabled provider and an unknown name make no outbound call at all");
    }

    [Fact]
    public async Task The_providers_list_names_the_configured_oidc_providers_and_only_those()
    {
        var response = await _client.GetAsync("/api/auth/providers", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        var listed = document.RootElement.GetProperty("oidc").EnumerateArray()
            .Select(p => (Name: p.GetProperty("name").GetString(), DisplayName: p.GetProperty("displayName").GetString()))
            .ToList();
        listed.Should().HaveCount(3);
        listed.Should().Equal(("broken", "broken"), ("entra", "entra"), ("stub", "Stub ID"));
        document.RootElement.GetProperty("github").GetBoolean().Should().BeFalse("the existing fields are still there");
        body.Should().NotContain(OidcStubProvider.ClientSecret).And.NotContain(OidcStubProvider.ClientId);
    }

    [Fact]
    public async Task The_master_switch_takes_the_oidc_providers_off_too()
    {
        var settings = Settings();
        settings["ExternalAuth:Enabled"] = "false";
        var client = ClientOf(_fixture.WithSettings(settings));

        (await client.GetAsync("/api/auth/oidc/stub/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var document = JsonDocument.Parse(await (await client.GetAsync(
            "/api/auth/providers", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("oidc").GetArrayLength().Should().Be(0);
    }

    // ---- the sign-in itself ----

    [Fact]
    public async Task A_valid_id_token_signs_a_new_person_in_with_a_tenant_scoped_token_and_links_them()
    {
        var subject = NewSubject();
        var email = NewEmail();

        var response = await SignInAsync(started => Token(started, subject, email));

        ShouldBeSignedIn(response);
        var user = await UserByEmailAsync(email);
        user.Should().NotBeNull("a first sign-in with a verified address creates the account");

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(AccessTokenFrom(response));
        jwt.Claims.Should().Contain(c => c.Type == "UserId" && c.Value == user!.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "tenant" && c.Value == barakoCMS.Models.Tenant.DefaultSlug,
            "the token comes from core's issuer, which is where the tenant is decided");
        jwt.Claims.Should().Contain(c => c.Type == "jti");

        var link = await LinkAsync(OidcStubProvider.Authority, subject);
        link.Should().NotBeNull("the provider account is recorded by issuer and subject");
        link!.UserId.Should().Be(user!.Id);
        link.Provider.Should().Be("stub");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessTokenFrom(response));
        var profile = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        profile.StatusCode.Should().Be(HttpStatusCode.OK, "the API accepts the token it issued");
        using var document = JsonDocument.Parse(await profile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("name").GetString().Should().Be("Stub Person");
    }

    [Fact]
    public async Task The_code_exchange_carries_the_verifier_whose_hash_the_start_sent()
    {
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), started.Challenge);

        ShouldBeSignedIn(await CallbackAsync(code, started.State, started.Cookie));

        var exchange = Stub.TokenRequests[code];
        exchange.Form["grant_type"].Should().Be("authorization_code");
        OidcTestTokens.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(exchange.Form["code_verifier"])))
            .Should().Be(started.Challenge);
        exchange.Authorization.Should().Be(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OidcStubProvider.ClientId}:{OidcStubProvider.ClientSecret}")),
            "a discovery document that lists no method means HTTP Basic");
        exchange.Form.Should().NotContainKey("client_secret");
    }

    [Fact]
    public async Task A_code_issued_under_another_browsers_challenge_is_not_redeemed()
    {
        var started = await StartAsync();
        var other = await StartAsync();
        var email = NewEmail();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), email), other.Challenge);

        var response = await CallbackAsync(code, started.State, started.Cookie);

        ShouldBeRefused(response, "the stub checks the verifier against the challenge the code was issued under");
        Stub.TokenRequests[code].Times.Should().Be(1, "the refusal came from the provider, so the exchange was attempted");
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task Every_outbound_call_goes_through_the_guarded_client()
    {
        ShouldBeSignedIn(await SignInAsync(started => Token(started, NewSubject(), NewEmail())));

        // The factory belongs to the whole host, and other services create clients of their own
        // while this runs. What is asserted is who sent the requests that reached a provider.
        var requests = _clients!.Requests.ToList();
        var toAProvider = requests.Where(r => IsProviderUrl(r.Url)).ToList();
        toAProvider.Should().NotBeEmpty();
        toAProvider.Should().Contain(r => r.Url == OidcStubProvider.Authority + "/token", "the sign-in above exchanged a code");
        toAProvider.Should().OnlyContain(r => r.Client == "ExternalApi",
            "that client's handler is the one that refuses internal addresses and follows no redirect");

        // Matched by path and not by client name, since another service on the host may also use
        // the guarded client for calls of its own.
        var oidcShaped = requests.Where(r =>
            r.Url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
            || r.Url.EndsWith("/keys", StringComparison.Ordinal)
            || r.Url.EndsWith("/token", StringComparison.Ordinal)).ToList();
        oidcShaped.Should().NotBeEmpty();
        oidcShaped.Should().OnlyContain(r => IsProviderUrl(r.Url),
            "discovery, keys and the exchange go to the configured provider over https and nowhere else");
    }

    // ---- state ----

    [Fact]
    public async Task A_callback_with_no_state_cookie_is_refused()
    {
        var started = await StartAsync();
        var email = NewEmail();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), email), null);

        ShouldBeRefused(await CallbackAsync(code, started.State, cookie: null), "this browser never began the flow");

        Stub.TokenRequests.ContainsKey(code).Should().BeFalse("the code is not sent anywhere before the state is checked");
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task A_callback_whose_state_is_not_the_one_in_the_cookie_is_refused()
    {
        var started = await StartAsync();
        var other = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), null);

        ShouldBeRefused(await CallbackAsync(code, other.State, started.Cookie), "the state belongs to another flow");

        Stub.TokenRequests.ContainsKey(code).Should().BeFalse("the code is not sent anywhere before the state is checked");
    }

    [Fact]
    public async Task A_state_works_once()
    {
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), started.Challenge);

        ShouldBeSignedIn(await CallbackAsync(code, started.State, started.Cookie));
        var replay = await CallbackAsync(code, started.State, started.Cookie);

        ShouldBeRefused(replay, "the stub would redeem the code again, so the refusal is ours");
        Stub.TokenRequests[code].Times.Should().Be(1, "a spent state is refused before the code is sent");
    }

    [Fact]
    public async Task Every_callback_expires_the_state_cookie_in_a_form_the_browser_accepts()
    {
        var started = await StartAsync();

        var response = await CallbackAsync(NewCode(), started.State, started.Cookie);

        var removals = response.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith("__Host-oidc_stub", StringComparison.Ordinal))
            .Select(c => c.ToLowerInvariant())
            .ToList();
        removals.Should().HaveCount(2);
        removals.Should().OnlyContain(c => c.Contains("expires=thu, 01 jan 1970") && c.Contains("secure") && c.Contains("path=/"),
            "a __Host- cookie can only be replaced by one that is Secure with Path=/, so a bare removal would be ignored");
    }

    // ---- what the id token has to prove ----

    public enum Flaw
    {
        AnotherNonce,
        NoNonce,
        WrongAudience,
        WrongIssuer,
        Expired,
        NotYetValid,
        SignedByAnotherKey,
        Unsigned,
        HmacWithTheClientSecret,
    }

    [Theory]
    [InlineData(Flaw.AnotherNonce)]
    [InlineData(Flaw.NoNonce)]
    [InlineData(Flaw.WrongAudience)]
    [InlineData(Flaw.WrongIssuer)]
    [InlineData(Flaw.Expired)]
    [InlineData(Flaw.NotYetValid)]
    [InlineData(Flaw.SignedByAnotherKey)]
    [InlineData(Flaw.Unsigned)]
    [InlineData(Flaw.HmacWithTheClientSecret)]
    public async Task An_id_token_with_one_thing_wrong_signs_nobody_in(Flaw flaw)
    {
        var subject = NewSubject();
        var email = NewEmail();
        using var otherKey = RSA.Create(2048);

        var response = await SignInAsync(started =>
        {
            var claims = OidcTestTokens.Claims(started.Nonce, subject, email);
            switch (flaw)
            {
                case Flaw.AnotherNonce: claims["nonce"] = OidcFlow.New().Nonce; break;
                case Flaw.NoNonce: claims.Remove("nonce"); break;
                case Flaw.WrongAudience: claims["aud"] = "some-other-client"; break;
                case Flaw.WrongIssuer: claims["iss"] = OidcStubProvider.Authority + "/"; break;
                case Flaw.Expired: claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(); break;
                case Flaw.NotYetValid: claims["nbf"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(); break;
            }

            return flaw switch
            {
                Flaw.SignedByAnotherKey => OidcTestTokens.Rs256(otherKey, claims),
                Flaw.Unsigned => OidcTestTokens.Unsigned(claims),
                Flaw.HmacWithTheClientSecret => OidcTestTokens.Hs256(Encoding.UTF8.GetBytes(OidcStubProvider.ClientSecret), claims),
                _ => OidcTestTokens.Rs256(Stub.Key, claims),
            };
        });

        ShouldBeRefused(response, $"{flaw} is not a token this provider issued to this client for this request");
        Uri.UnescapeDataString(response.Headers.Location!.ToString()).Should().Contain("could not be verified");
        (await UserByEmailAsync(email)).Should().BeNull("a refused token creates no account");
        (await LinkAsync(OidcStubProvider.Authority, subject)).Should().BeNull("and links nothing");
    }

    // ---- which account ----

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_unverified_email_is_not_linked_to_the_account_that_holds_it(bool? emailVerified)
    {
        var victim = await CreateUserAsync(NewEmail());
        var subject = NewSubject();

        var response = await SignInAsync(started => Token(started, subject, victim.Email, claims =>
        {
            if (emailVerified is null) claims.Remove("email_verified");
            else claims["email_verified"] = emailVerified.Value;
        }));

        ShouldBeRefused(response, "the provider did not vouch for the address, so it names nobody here");
        Uri.UnescapeDataString(response.Headers.Location!.ToString()).Should().Contain("verified email");
        (await LinkAsync(OidcStubProvider.Authority, subject)).Should().BeNull();
    }

    [Fact]
    public async Task An_unverified_email_nobody_holds_creates_no_account()
    {
        var email = NewEmail();
        var subject = NewSubject();

        var response = await SignInAsync(started => Token(started, subject, email, claims => claims["email_verified"] = false));

        ShouldBeRefused(response, "an unverified address must not be squatted either");
        (await UserByEmailAsync(email)).Should().BeNull();
        (await LinkAsync(OidcStubProvider.Authority, subject)).Should().BeNull();
    }

    [Fact]
    public async Task A_verified_email_links_the_provider_account_to_the_user_that_holds_it()
    {
        var existing = await CreateUserAsync(NewEmail());
        var subject = NewSubject();

        var response = await SignInAsync(started => Token(started, subject, existing.Email.ToUpperInvariant()));

        ShouldBeSignedIn(response);
        (await LinkAsync(OidcStubProvider.Authority, subject))!.UserId.Should().Be(existing.Id,
            "the address is compared the way the user index stores it, so case does not make a second account");
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(AccessTokenFrom(response))
            .Claims.Should().Contain(c => c.Type == "UserId" && c.Value == existing.Id.ToString());
    }

    [Fact]
    public async Task A_linked_account_is_found_by_issuer_and_subject_and_not_by_the_email_it_now_reports()
    {
        var subject = NewSubject();
        var firstEmail = NewEmail();
        ShouldBeSignedIn(await SignInAsync(started => Token(started, subject, firstEmail)));
        var linked = await UserByEmailAsync(firstEmail);
        var bystander = await CreateUserAsync(NewEmail());

        // The provider account now reports the bystander's address, unverified. By email alone this
        // would be refused, or worse, be the bystander.
        var response = await SignInAsync(started => Token(started, subject, bystander.Email,
            claims => claims["email_verified"] = false));

        ShouldBeSignedIn(response);
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(AccessTokenFrom(response))
            .Claims.Should().Contain(c => c.Type == "UserId" && c.Value == linked!.Id.ToString());
        (await UserByEmailAsync(firstEmail))!.Email.Should().Be(firstEmail, "the local address is not rewritten from the token");
    }

    [Fact]
    public async Task A_linked_account_with_a_second_factor_is_challenged_and_given_no_token()
    {
        var user = await CreateUserAsync(NewEmail(), mfaEnrolled: true);

        var response = await SignInAsync(started => Token(started, NewSubject(), user.Email));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/auth/social#mfa_challenge=");
        location.Should().NotContain("token=").And.NotContain("refresh=");
    }

    [Fact]
    public async Task A_club_the_person_does_not_belong_to_is_refused_by_the_token_issuer()
    {
        string slug;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            slug = $"club-{Guid.NewGuid():N}".ToLowerInvariant();
            session.Store(new barakoCMS.Models.Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await SignInAsync(started => Token(started, NewSubject(), NewEmail()), club: slug);

        ShouldBeRefused(response, "proving an identity is not membership of a tenant");
        Uri.UnescapeDataString(response.Headers.Location!.ToString()).Should().Contain("not a member");
        response.Headers.Location!.ToString().Should().EndWith($"&club={slug}");
    }

    // ---- an issuer that is a template ----

    private string SharedToken(Started started, string directory, string subject, string email, Action<Dictionary<string, object?>>? edit = null)
    {
        var claims = OidcTestTokens.Claims(started.Nonce, subject, email);
        claims["iss"] = OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", directory);
        claims["tid"] = directory;
        claims.Remove("email_verified");
        claims["xms_edov"] = true;
        edit?.Invoke(claims);
        return OidcTestTokens.Rs256(Stub.Key, claims);
    }

    [Fact]
    public async Task A_template_issuer_accepts_the_directory_the_token_names_and_links_by_that_directory()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var subject = NewSubject();
        var firstEmail = NewEmail();
        var secondEmail = NewEmail();

        ShouldBeSignedIn(await SignInAsync(started => SharedToken(started, first, subject, firstEmail), provider: "entra"));
        ShouldBeSignedIn(await SignInAsync(started => SharedToken(started, second, subject, secondEmail), provider: "entra"));

        var firstLink = await LinkAsync(OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", first), subject);
        var secondLink = await LinkAsync(OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", second), subject);
        firstLink.Should().NotBeNull();
        secondLink.Should().NotBeNull();
        secondLink!.UserId.Should().NotBe(firstLink!.UserId,
            "the same subject in two directories is two people, because the subject is only unique within its issuer");
    }

    [Fact]
    public async Task A_template_issuer_takes_the_client_secret_in_the_form_when_the_provider_says_so()
    {
        var directory = Guid.NewGuid().ToString();
        var started = await StartAsync("entra");
        var code = NewCode();
        Stub.Codes[code] = (SharedToken(started, directory, NewSubject(), NewEmail()), started.Challenge);

        ShouldBeSignedIn(await CallbackAsync(code, started.State, started.Cookie, "entra"));

        started.Location.GetLeftPart(UriPartial.Path).Should().Be(OidcStubProvider.SharedAuthority + "/authorize");
        Stub.TokenRequests[code].Form["client_secret"].Should().Be(OidcStubProvider.ClientSecret);
        Stub.TokenRequests[code].Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("another-directory")]
    [InlineData("no-directory")]
    [InlineData("directory-is-not-a-guid")]
    [InlineData("standard-claim-only")]
    public async Task A_template_issuer_still_refuses_what_the_template_does_not_allow(string flaw)
    {
        var directory = Guid.NewGuid().ToString();
        var subject = NewSubject();
        var email = NewEmail();

        var response = await SignInAsync(started => SharedToken(started, directory, subject, email, claims =>
        {
            switch (flaw)
            {
                case "another-directory":
                    claims["iss"] = OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", Guid.NewGuid().ToString());
                    break;
                case "no-directory":
                    claims.Remove("tid");
                    break;
                case "directory-is-not-a-guid":
                    claims["tid"] = "common";
                    claims["iss"] = OidcStubProvider.SharedIssuerTemplate.Replace("{tenantid}", "common");
                    break;
                case "standard-claim-only":
                    claims.Remove("xms_edov");
                    claims["email_verified"] = true;
                    break;
            }
        }), provider: "entra");

        ShouldBeRefused(response, flaw);
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    // ---- the branches around the happy path ----

    [Fact]
    public async Task A_link_whose_user_was_deleted_is_treated_as_no_link()
    {
        var subject = NewSubject();
        var firstEmail = NewEmail();
        ShouldBeSignedIn(await SignInAsync(started => Token(started, subject, firstEmail)));
        var removed = await UserByEmailAsync(firstEmail);
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<barakoCMS.Models.User>(removed!.Id);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var secondEmail = NewEmail();
        ShouldBeRefused(
            await SignInAsync(started => Token(started, subject, secondEmail, claims => claims["email_verified"] = false)),
            "with its user gone the link names nobody, so this is a first sign-in again and needs a verified address");
        (await UserByEmailAsync(secondEmail)).Should().BeNull();

        ShouldBeSignedIn(await SignInAsync(started => Token(started, subject, secondEmail)));

        var recreated = await UserByEmailAsync(secondEmail);
        recreated.Should().NotBeNull();
        recreated!.Id.Should().NotBe(removed.Id, "the deleted account is not brought back");
        (await LinkAsync(OidcStubProvider.Authority, subject))!.UserId.Should().Be(recreated.Id,
            "the one row for this provider account now points at the new user");
    }

    [Fact]
    public async Task An_address_already_taken_as_a_username_still_gets_its_own_account()
    {
        var email = NewEmail();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new barakoCMS.Models.User
            {
                Id = Guid.NewGuid(),
                Email = NewEmail(),
                Username = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("a-real-password"),
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ShouldBeSignedIn(await SignInAsync(started => Token(started, NewSubject(), email)));

        var created = await UserByEmailAsync(email);
        created.Should().NotBeNull("the username index must not turn a first sign-in into an error");
        created!.Username.Should().StartWith(email + "+");
        created.Username.Should().HaveLength(email.Length + 9);
    }

    /// <summary>
    /// Six first sign-ins for one address at once. The unique index on the user's email lets one
    /// insert through, and a loser is sent back to try again instead of answering 500.
    /// </summary>
    /// <remarks>
    /// Whether any request actually loses depends on timing, so this pins the outcome and not the
    /// path: no server error, one account. The test after it drives the losing path directly.
    /// </remarks>
    [Fact]
    public async Task First_sign_ins_racing_for_one_address_make_one_account_and_no_server_error()
    {
        var email = NewEmail();
        var flows = new List<(Started Started, string Code)>();
        for (var i = 0; i < 6; i++)
        {
            var started = await StartAsync();
            var code = NewCode();
            Stub.Codes[code] = (Token(started, NewSubject(), email), started.Challenge);
            flows.Add((started, code));
        }

        var responses = await Task.WhenAll(flows.Select(f => CallbackAsync(f.Code, f.Started.State, f.Started.Cookie)));

        responses.Should().HaveCount(6);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Redirect);
        responses.Count(r => r.Headers.Location!.ToString().Contains("#token=")).Should().BeGreaterThan(0);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var users = await session.Query<barakoCMS.Models.User>()
            .Where(u => u.Email == email).ToListAsync(TestContext.Current.CancellationToken);
        users.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_second_user_for_one_address_is_stopped_by_the_user_index_and_recognised_as_that()
    {
        var email = NewEmail();
        var subject = NewSubject();
        using var scope = _fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var session = services.GetRequiredService<IDocumentSession>();

        // The racing request's user, written by this unit of work but invisible to the lookup below,
        // exactly as another request's uncommitted insert would be.
        session.Store(new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Username = $"rival-{Guid.NewGuid():n}",
            PasswordHash = "",
        });
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "oidc-sign-in-tests";

        var act = () => SocialSignIn.IssueForIdentityAsync(
            session,
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            services.GetRequiredService<barakoCMS.Core.Interfaces.IDeviceGate>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>(),
            services.GetRequiredService<barakoCMS.Infrastructure.Auth.Mfa.IMfaService>(),
            context,
            new OidcIdentity(OidcStubProvider.Authority, subject, email, true, null, null),
            "stub",
            "",
            TestContext.Current.CancellationToken);

        var thrown = await act.Should().ThrowAsync<Exception>();
        OidcSupport.IsUniqueViolation(thrown.Which).Should().BeTrue(
            "this is the exception the callback turns into a try-again redirect");
        (await UserByEmailAsync(email)).Should().BeNull("the whole unit of work failed, so neither user exists");
        (await LinkAsync(OidcStubProvider.Authority, subject)).Should().BeNull("and no link was left pointing at one");
    }

    [Fact]
    public async Task A_callback_carrying_an_error_and_no_code_is_refused_without_echoing_it()
    {
        var started = await StartAsync();
        var exchangesBefore = Stub.TokenRequests.Count;
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/auth/oidc/stub/callback?error=access_denied&error_description=marker-from-the-request&state={started.State}");
        request.Headers.Add("Cookie", $"__Host-oidc_stub={started.Cookie}; __Host-oidc_stub_club=");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        ShouldBeRefused(response, "the person cancelled at the provider, or the provider refused");
        var location = Uri.UnescapeDataString(response.Headers.Location!.ToString());
        location.Should().NotContain("access_denied").And.NotContain("marker-from-the-request");
        Stub.TokenRequests.Count.Should().Be(exchangesBefore, "there is no code to exchange");
        response.Headers.GetValues("Set-Cookie")
            .Count(c => c.StartsWith("__Host-oidc_stub", StringComparison.Ordinal)).Should().Be(2, "the state is spent either way");
    }

    [Fact]
    public async Task A_club_longer_than_a_hundred_characters_is_not_carried()
    {
        var tooLong = await _client.GetAsync(
            $"/api/auth/oidc/stub/start?club={new string('a', OidcSupport.MaxClubLength + 1)}", TestContext.Current.CancellationToken);
        tooLong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        tooLong.Headers.Contains("Set-Cookie").Should().BeFalse("nothing is started for it");

        (await _client.GetAsync(
            $"/api/auth/oidc/stub/start?club={new string('a', OidcSupport.MaxClubLength)}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Redirect, "a hundred is inside the bound");

        // The start never sets one this long, so a cookie that is must have been written by hand.
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), started.Challenge);
        var response = await CallbackAsync(code, started.State, started.Cookie, club: new string('b', OidcSupport.MaxClubLength + 1));

        ShouldBeSignedIn(response);
        response.Headers.Location!.ToString().Should().EndWith("&club=", "an oversized club is dropped, not passed on");
    }

    [Fact]
    public async Task A_start_for_a_provider_whose_discovery_fails_sends_the_browser_back_with_a_message()
    {
        var response = await _client.GetAsync("/api/auth/oidc/broken/start?club=some-club", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = Uri.UnescapeDataString(response.Headers.Location!.ToString());
        location.Should().StartWith($"{BaseUrl}/login?fberror=");
        location.Should().Contain("reach broken").And.EndWith("&club=some-club");
        response.Headers.Contains("Set-Cookie").Should().BeFalse("no state is minted for a flow that cannot begin");
    }

    [Fact]
    public async Task One_providers_state_cookie_is_not_accepted_at_another_providers_callback()
    {
        var started = await StartAsync("stub");
        var code = NewCode();
        Stub.Codes[code] = (Token(started, NewSubject(), NewEmail()), null);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/oidc/entra/callback?code={code}&state={started.State}");
        request.Headers.Add("Cookie", $"__Host-oidc_stub={started.Cookie}; __Host-oidc_stub_club=");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        ShouldBeRefused(response, "each provider reads its own cookie, so a flow begun at one cannot finish at another");
        Stub.TokenRequests.ContainsKey(code).Should().BeFalse();
    }

    // ---- limits ----

    [Fact]
    public async Task Start_and_callback_share_one_rate_limit_per_client_address()
    {
        for (var i = 0; i < OidcSupport.DefaultPermitLimit; i++)
        {
            (await _client.GetAsync("/api/auth/oidc/stub/start", TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.Redirect, "request {0} is inside the limit", i + 1);
        }

        (await _client.GetAsync("/api/auth/oidc/stub/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await _client.GetAsync("/api/auth/oidc/stub/callback?code=c&state=s", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the callback draws on the same bucket");

        var another = ClientOf(_host!);
        (await another.GetAsync("/api/auth/oidc/stub/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Redirect, "the bucket is per client address");
    }

    [Fact]
    public async Task The_client_secret_is_in_no_redirect_and_no_cookie()
    {
        var started = await StartAsync();
        var refused = await CallbackAsync(NewCode(), started.State, started.Cookie);

        foreach (var response in new[] { started.Response, refused })
        {
            response.Headers.Location!.ToString().Should().NotContain(OidcStubProvider.ClientSecret);
            string.Join('\n', response.Headers.GetValues("Set-Cookie")).Should().NotContain(OidcStubProvider.ClientSecret);
        }
    }
}
