using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// Sign-in through a provider that answers with a form post and takes a signed client secret, the
/// way Apple does, against a stub issuer (#786).
/// </summary>
/// <remarks>
/// Cookies are attached by hand, as in <see cref="OidcSignInTests"/>: they are <c>Secure</c> and the
/// test host speaks plain HTTP.
/// </remarks>
[Collection("Sequential")]
public class OidcFormPostCallbackTests
{
    private const string BaseUrl = "https://cms.test.example";
    private const string TeamId = "TEAM456789";
    private const string KeyId = "ABC123DEFG";

    private static readonly Lock Gate = new();
    private static readonly ECDsa SecretKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static WebApplicationFactory<Program>? _host;
    private static OidcStubProvider? _stub;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public OidcFormPostCallbackTests(IntegrationTestFixture fixture)
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
            }
        }

        _client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
    }

    private static OidcStubProvider Stub => _stub!;

    private static Dictionary<string, string?> Settings() => new()
    {
        { "App:BaseUrl", BaseUrl },
        { "Oidc:Providers:apple:Authority", OidcStubProvider.AppleAuthority },
        { "Oidc:Providers:apple:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:apple:DisplayName", "Apple" },
        { "Oidc:Providers:apple:Scopes", "openid name email" },
        { "Oidc:Providers:apple:ResponseMode", "form_post" },
        { "Oidc:Providers:apple:SignedClientSecret:KeyId", KeyId },
        { "Oidc:Providers:apple:SignedClientSecret:TeamId", TeamId },
        { "Oidc:Providers:apple:SignedClientSecret:PrivateKey", SecretKey.ExportPkcs8PrivateKeyPem() },
        { "Oidc:Providers:stub:Authority", OidcStubProvider.Authority },
        { "Oidc:Providers:stub:ClientId", OidcStubProvider.ClientId },
        { "Oidc:Providers:stub:ClientSecret", OidcStubProvider.ClientSecret },
        { "GitHub:ClientId", "" },
        { "Google:ClientId", "" },
        { "Facebook:AppId", "" },
        { "LinkedIn:ClientId", "" },
    };

    private sealed record Started(HttpResponseMessage Response, Uri Location, string State, string Nonce, string Challenge, string Cookie);

    private async Task<Started> StartAsync(string provider = "apple")
    {
        var response = await _client.GetAsync($"/api/auth/oidc/{provider}/start", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        var sent = System.Web.HttpUtility.ParseQueryString(location.Query);
        var prefix = $"__Host-oidc_{provider}=";
        var cookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return new Started(response, location, sent["state"]!, sent["nonce"]!, sent["code_challenge"]!, cookie.Split(';')[0][prefix.Length..]);
    }

    private async Task<HttpResponseMessage> PostCallbackAsync(
        Dictionary<string, string> form, string? cookie, string provider = "apple")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/oidc/{provider}/callback")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("Origin", OidcStubProvider.AppleAuthority);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"__Host-oidc_{provider}={cookie}; __Host-oidc_{provider}_club=");
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string NewCode() => "code-" + Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"apple-{Guid.NewGuid():n}@example.com";

    private static string AppleToken(Started started, string email)
    {
        var claims = OidcTestTokens.Claims(started.Nonce, "apple-" + Guid.NewGuid().ToString("N"), email);
        claims["iss"] = OidcStubProvider.AppleAuthority;
        claims.Remove("name");
        claims.Remove("picture");
        return OidcTestTokens.Rs256(Stub.Key, claims);
    }

    private async Task<barakoCMS.Models.User?> UserByEmailAsync(string email)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<barakoCMS.Models.User>()
            .FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken);
    }

    private static void ShouldBeRefused(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/login?fberror=", because);
        location.Should().NotContain("token=", because);
    }

    [Fact]
    public async Task The_start_asks_for_a_form_post_and_leaves_cookies_a_cross_site_post_carries()
    {
        var started = await StartAsync();

        var sent = System.Web.HttpUtility.ParseQueryString(started.Location.Query);
        sent["response_mode"].Should().Be("form_post");
        sent["scope"].Should().Be("openid name email");
        sent["redirect_uri"].Should().Be(BaseUrl + "/api/auth/oidc/apple/callback");

        var cookies = started.Response.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith("__Host-oidc_apple", StringComparison.Ordinal))
            .Select(c => c.ToLowerInvariant())
            .ToList();
        cookies.Should().HaveCount(2);
        cookies.Should().OnlyContain(c => c.Contains("samesite=none") && c.Contains("secure") && c.Contains("httponly") && c.Contains("path=/"),
            "a Lax cookie is not sent on the provider's cross-site POST, and None is only allowed with Secure");
    }

    [Fact]
    public async Task A_query_provider_keeps_lax_cookies_and_has_no_post_callback()
    {
        var started = await StartAsync("stub");

        System.Web.HttpUtility.ParseQueryString(started.Location.Query)["response_mode"].Should().BeNull();
        started.Response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("__Host-oidc_stub=", StringComparison.Ordinal))
            .ToLowerInvariant().Should().Contain("samesite=lax");

        var code = NewCode();
        Stub.Codes[code] = (AppleToken(started, NewEmail()), started.Challenge);
        var response = await PostCallbackAsync(new() { ["code"] = code, ["state"] = started.State }, started.Cookie, "stub");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "only a form_post provider takes a POST callback");
        Stub.TokenRequests.ContainsKey(code).Should().BeFalse();
    }

    [Fact]
    public async Task A_form_post_callback_signs_in_with_a_signed_client_secret()
    {
        var email = NewEmail();
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (AppleToken(started, email), started.Challenge);

        var response = await PostCallbackAsync(new()
        {
            ["code"] = code,
            ["state"] = started.State,
            ["id_token"] = "ignored",
            ["user"] = """{"name":{"firstName":"Ana","lastName":"Cruz"}}""",
        }, started.Cookie);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().StartWith($"{BaseUrl}/auth/social#token=");
        (await UserByEmailAsync(email)).Should().NotBeNull("a verified first sign-in creates the account");

        var removals = response.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith("__Host-oidc_apple", StringComparison.Ordinal))
            .Select(c => c.ToLowerInvariant())
            .ToList();
        removals.Should().HaveCount(2);
        removals.Should().OnlyContain(c => c.Contains("expires=thu, 01 jan 1970") && c.Contains("samesite=none"));

        var exchange = Stub.TokenRequests[code];
        exchange.Authorization.Should().BeNull("the provider takes the secret in the form only");
        exchange.Form["client_id"].Should().Be(OidcStubProvider.ClientId);
        var secret = exchange.Form["client_secret"];
        var parts = secret.Split('.');
        parts.Should().HaveCount(3, "the secret is a JWT, not a configured string");
        using var publicKey = ECDsa.Create(SecretKey.ExportParameters(includePrivateParameters: false));
        publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlEncoder.DecodeBytes(parts[2]), HashAlgorithmName.SHA256)
            .Should().BeTrue();
        using var claims = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
        claims.RootElement.GetProperty("iss").GetString().Should().Be(TeamId);
        claims.RootElement.GetProperty("sub").GetString().Should().Be(OidcStubProvider.ClientId);
        claims.RootElement.GetProperty("aud").GetString().Should().Be(OidcStubProvider.AppleAuthority);
    }

    [Fact]
    public async Task A_form_post_without_the_cookie_or_with_another_state_is_refused_before_the_code_is_sent()
    {
        var started = await StartAsync();
        var other = await StartAsync();
        var email = NewEmail();
        var noCookie = NewCode();
        var wrongState = NewCode();
        Stub.Codes[noCookie] = (AppleToken(started, email), started.Challenge);
        Stub.Codes[wrongState] = (AppleToken(started, email), started.Challenge);

        ShouldBeRefused(await PostCallbackAsync(new() { ["code"] = noCookie, ["state"] = started.State }, cookie: null),
            "a third site can post a code and state, but not the browser's cookie");
        ShouldBeRefused(await PostCallbackAsync(new() { ["code"] = wrongState, ["state"] = other.State }, started.Cookie),
            "the state belongs to another flow");

        Stub.TokenRequests.ContainsKey(noCookie).Should().BeFalse();
        Stub.TokenRequests.ContainsKey(wrongState).Should().BeFalse();
        (await UserByEmailAsync(email)).Should().BeNull();
    }

    [Fact]
    public async Task A_form_post_state_works_once()
    {
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (AppleToken(started, NewEmail()), started.Challenge);
        var form = new Dictionary<string, string> { ["code"] = code, ["state"] = started.State };

        (await PostCallbackAsync(form, started.Cookie)).Headers.Location!.ToString()
            .Should().StartWith($"{BaseUrl}/auth/social#token=");
        ShouldBeRefused(await PostCallbackAsync(form, started.Cookie), "the stub would redeem the code again, so the refusal is ours");

        Stub.TokenRequests[code].Times.Should().Be(1);
    }

    [Fact]
    public async Task A_field_sent_twice_counts_as_missing()
    {
        var started = await StartAsync();
        var code = NewCode();
        Stub.Codes[code] = (AppleToken(started, NewEmail()), started.Challenge);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/oidc/apple/callback")
        {
            Content = new StringContent($"code={code}&state={started.State}&state={started.State}",
                Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("Cookie", $"__Host-oidc_apple={started.Cookie}; __Host-oidc_apple_club=");

        ShouldBeRefused(await _client.SendAsync(request, TestContext.Current.CancellationToken),
            "two values for state leave no one value to compare");
        Stub.TokenRequests.ContainsKey(code).Should().BeFalse();
    }
}
