using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// The outbound half of OpenID Connect sign-in: what is fetched, from where, how often, and what is
/// done with an answer that cannot be trusted (#786).
/// </summary>
public class OidcBackchannelTests
{
    private readonly OidcStubProvider _stub = new();
    private readonly RecordingClientFactory _clients;
    private readonly CapturingLogger _log = new();
    private readonly OidcBackchannel _backchannel;
    private DateTimeOffset _now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public OidcBackchannelTests()
    {
        _clients = new RecordingClientFactory(_stub);
        _backchannel = new OidcBackchannel(_clients, _log) { Now = () => _now };
    }

    private sealed class CapturingLogger : ILogger<OidcBackchannel>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    private static OidcProvider Provider(string authority = OidcStubProvider.Authority, string? issuer = null) => new(
        "stub", "Stub ID", authority, issuer ?? authority,
        OidcStubProvider.ClientId, OidcStubProvider.ClientSecret, "openid email profile", "email_verified");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_endpoints_come_from_the_authoritys_discovery_document_through_the_guarded_client()
    {
        var endpoints = await _backchannel.EndpointsAsync(Provider(), Ct);

        endpoints.Should().NotBeNull();
        endpoints!.AuthorizationEndpoint.Should().Be(OidcStubProvider.Authority + "/authorize");
        endpoints.TokenEndpoint.Should().Be(OidcStubProvider.Authority + "/token");
        endpoints.JwksUri.Should().Be(OidcStubProvider.Authority + "/keys");
        endpoints.SecretInBody.Should().BeFalse("a document that lists no method means HTTP Basic");
        _stub.RequestedUrls.Should().Equal(OidcStubProvider.Authority + "/.well-known/openid-configuration");
        _clients.Names.Should().Equal("ExternalApi");
    }

    [Fact]
    public async Task A_trailing_slash_on_the_authority_is_dropped_from_the_discovery_address_only()
    {
        _stub.DiscoveryIssuer = OidcStubProvider.Authority + "/";

        var endpoints = await _backchannel.EndpointsAsync(Provider(OidcStubProvider.Authority + "/"), Ct);

        endpoints.Should().NotBeNull("the issuer is compared as configured, slash included");
        _stub.RequestedUrls.Should().Equal(OidcStubProvider.Authority + "/.well-known/openid-configuration");
    }

    [Fact]
    public async Task Discovery_is_fetched_once_and_again_only_after_its_lifetime()
    {
        await _backchannel.EndpointsAsync(Provider(), Ct);
        await _backchannel.EndpointsAsync(Provider(), Ct);
        _now += OidcBackchannel.Lifetime - TimeSpan.FromSeconds(1);
        await _backchannel.EndpointsAsync(Provider(), Ct);
        _stub.DiscoveryCalls.Should().Be(1);

        _now += TimeSpan.FromSeconds(2);
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().NotBeNull();
        _stub.DiscoveryCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_discovery_document_naming_another_issuer_is_not_used()
    {
        _stub.DiscoveryIssuer = "https://somebody-else.example";

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();

        _stub.KeysCalls.Should().Be(0);
        _log.Lines.Should().ContainSingle().Which.Should().Contain("issuer");
    }

    [Theory]
    [InlineData("authorization_endpoint", "http://idp.test.example/authorize")]
    [InlineData("token_endpoint", "http://169.254.169.254/latest/meta-data")]
    [InlineData("jwks_uri", "file:///etc/passwd")]
    [InlineData("jwks_uri", "/keys")]
    [InlineData("token_endpoint", "https://user:pass@idp.test.example/token")]
    public async Task A_discovery_document_naming_an_endpoint_that_is_not_https_is_not_used(string property, string value)
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, object>>(
            OidcStubProvider.Discovery(OidcStubProvider.Authority, OidcStubProvider.Authority, postSecret: false))!;
        document[property] = value;
        _stub.DiscoveryBody = JsonSerializer.Serialize(document);

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();

        _stub.RequestedUrls.Should().HaveCount(1, "only the discovery document was fetched, and nothing it pointed at");
    }

    [Fact]
    public async Task A_provider_that_only_takes_a_signed_client_assertion_is_not_used()
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, object>>(
            OidcStubProvider.Discovery(OidcStubProvider.Authority, OidcStubProvider.Authority, postSecret: false))!;
        document["token_endpoint_auth_methods_supported"] = new[] { "private_key_jwt" };
        _stub.DiscoveryBody = JsonSerializer.Serialize(document);

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull(
            "a client secret is all this can present, so the exchange could only fail");
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("<html>sign in</html>")]
    [InlineData("")]
    public async Task A_discovery_answer_that_is_not_a_json_object_is_not_used(string body)
    {
        _stub.DiscoveryBody = body;

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_discovery_document_past_the_size_bound_is_not_used()
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, object>>(
            OidcStubProvider.Discovery(OidcStubProvider.Authority, OidcStubProvider.Authority, postSecret: false))!;
        document["padding"] = new string('x', OidcBackchannel.MaxMetadataBytes);
        _stub.DiscoveryBody = JsonSerializer.Serialize(document);

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();

        _stub.DiscoveryBody = null;
        _now += OidcBackchannel.FailureBackoff + TimeSpan.FromSeconds(1);
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().NotBeNull("the same document without the padding is the control");
    }

    [Fact]
    public async Task A_failed_fetch_is_not_repeated_until_the_backoff_has_passed()
    {
        _stub.DiscoveryIssuer = "https://somebody-else.example";
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();
        _stub.DiscoveryCalls.Should().Be(1, "three sign-in attempts against a broken provider cost one outbound call");

        _stub.DiscoveryIssuer = OidcStubProvider.Authority;
        _now += OidcBackchannel.FailureBackoff + TimeSpan.FromSeconds(1);
        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().NotBeNull();
        _stub.DiscoveryCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_provider_that_never_answers_is_given_up_on_at_the_timeout()
    {
        _stub.Hang = new TaskCompletionSource();
        _backchannel.Timeout = TimeSpan.FromMilliseconds(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        (await _backchannel.EndpointsAsync(Provider(), Ct)).Should().BeNull();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Keys_are_cached_and_an_unknown_key_id_refetches_them_at_most_once_per_interval()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;

        var keys = await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct);
        keys.Should().HaveCount(1);
        keys[0].Kid.Should().Be(OidcStubProvider.KeyId);
        await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct);
        await _backchannel.SigningKeysAsync(provider, endpoints, null, Ct);
        _stub.KeysCalls.Should().Be(1);

        for (var i = 0; i < 5; i++)
        {
            await _backchannel.SigningKeysAsync(provider, endpoints, $"unknown-{i}", Ct);
        }

        _stub.KeysCalls.Should().Be(1, "the keys were fetched a moment ago, so an unknown key id is just unknown");

        _now += OidcBackchannel.KeyRefreshInterval + TimeSpan.FromSeconds(1);
        for (var i = 0; i < 5; i++)
        {
            await _backchannel.SigningKeysAsync(provider, endpoints, $"unknown-{i}", Ct);
        }

        _stub.KeysCalls.Should().Be(2, "one refetch picks up a rotation, and the next four wait their turn");
    }

    [Fact]
    public async Task Only_asymmetric_signing_keys_are_kept_and_only_so_many()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var n = OidcTestTokens.Base64Url(parameters.Modulus!);
        var e = OidcTestTokens.Base64Url(parameters.Exponent!);

        var keys = new List<object>
        {
            new { kty = "oct", kid = "shared-secret", k = OidcTestTokens.Base64Url(new byte[32]) },
            new { kty = "RSA", use = "enc", kid = "encryption-only", n, e },
        };
        keys.AddRange(Enumerable.Range(0, OidcBackchannel.MaxKeys + 10).Select(i => (object)new { kty = "RSA", use = "sig", kid = $"k{i}", n, e }));
        _stub.KeysBody = JsonSerializer.Serialize(new { keys });

        var kept = await _backchannel.SigningKeysAsync(provider, endpoints, null, Ct);

        kept.Should().HaveCount(OidcBackchannel.MaxKeys);
        kept.Should().OnlyContain(k => k.Kty == "RSA" && k.Use == "sig");
    }

    [Fact]
    public async Task Keys_that_cannot_be_fetched_leave_nothing_to_verify_against()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;
        _stub.KeysBody = "not json";

        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Keys_already_held_are_used_while_the_key_endpoint_fails_and_only_up_to_the_ceiling()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;
        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().HaveCount(1);
        _stub.KeysBody = "not json";

        _now += OidcBackchannel.Lifetime + TimeSpan.FromSeconds(1);
        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().HaveCount(1,
            "past their lifetime the keys are refetched, and when that fails the ones held are still used");
        _stub.KeysCalls.Should().Be(2, "the refetch was attempted");

        _now = _now - OidcBackchannel.Lifetime - TimeSpan.FromSeconds(1) + OidcBackchannel.StaleKeyCeiling - TimeSpan.FromSeconds(1);
        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().HaveCount(1,
            "one second short of the ceiling they are still used");

        _now += TimeSpan.FromSeconds(2);
        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().BeEmpty(
            "past the ceiling there is nothing to verify against, so every token is refused");

        _stub.KeysBody = null;
        _now += OidcBackchannel.FailureBackoff + TimeSpan.FromSeconds(1);
        (await _backchannel.SigningKeysAsync(provider, endpoints, OidcStubProvider.KeyId, Ct)).Should().HaveCount(1,
            "and when the endpoint answers again the keys are back");
    }

    [Fact]
    public async Task A_provider_that_hangs_does_not_hold_up_another_providers_fetch()
    {
        const string slowAuthority = "https://slow.test.example";
        _stub.Hang = new TaskCompletionSource();
        _stub.HangPrefix = slowAuthority;
        _backchannel.Timeout = TimeSpan.FromSeconds(30);

        var slow = _backchannel.EndpointsAsync(Provider(slowAuthority), Ct);
        var other = await _backchannel.EndpointsAsync(Provider(), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        other.Should().NotBeNull("the second provider's fetch ran while the first was still waiting");
        slow.IsCompleted.Should().BeFalse("the first provider has not answered");

        _stub.Hang.SetResult();
        (await slow).Should().BeNull("the stub has no such authority, so once released the fetch fails");
    }

    [Fact]
    public async Task A_refused_exchange_returns_nothing_and_logs_neither_the_secret_nor_the_answer()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;

        var idToken = await _backchannel.ExchangeCodeAsync(
            provider, endpoints, "a-code-the-provider-never-issued", "https://cms.test.example/cb", "verifier", Ct);

        idToken.Should().BeNull();
        _stub.TokenRequests.ContainsKey("a-code-the-provider-never-issued").Should().BeTrue("the exchange was attempted");
        _log.Lines.Should().HaveCount(1);
        _log.Lines.Should().OnlyContain(line =>
            !line.Contains(OidcStubProvider.ClientSecret) && !line.Contains("invalid_grant") && !line.Contains("verifier"));
        _clients.Names.Should().HaveCount(2);
        _clients.Names.Should().OnlyContain(name => name == "ExternalApi");
    }

    [Fact]
    public async Task An_exchange_that_succeeds_returns_the_id_token()
    {
        var provider = Provider();
        var endpoints = (await _backchannel.EndpointsAsync(provider, Ct))!;
        _stub.Codes["good-code"] = ("header.payload.signature", null);

        (await _backchannel.ExchangeCodeAsync(provider, endpoints, "good-code", "https://cms.test.example/cb", "verifier", Ct))
            .Should().Be("header.payload.signature");
        _stub.TokenRequests["good-code"].Form["redirect_uri"].Should().Be("https://cms.test.example/cb");
        _stub.TokenRequests["good-code"].Form["code_verifier"].Should().Be("verifier");
    }
}
