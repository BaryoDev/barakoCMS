using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// What Apple needs that other providers do not: a client secret that is a signed JWT, and an
/// <c>email_verified</c> claim that may be text (#786).
/// </summary>
public class OidcAppleTests : IDisposable
{
    private const string KeyId = "ABC123DEFG";
    private const string TeamId = "TEAM456789";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private DateTimeOffset _now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _key.Dispose();

    private string Pem => _key.ExportPkcs8PrivateKeyPem();

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private Dictionary<string, string?> AppleSettings(string? pem = null) => new()
    {
        { "Oidc:Providers:apple:Authority", OidcStubProvider.AppleAuthority },
        { "Oidc:Providers:apple:ClientId", "com.example.web" },
        { "Oidc:Providers:apple:ResponseMode", "form_post" },
        { "Oidc:Providers:apple:SignedClientSecret:KeyId", KeyId },
        { "Oidc:Providers:apple:SignedClientSecret:TeamId", TeamId },
        { "Oidc:Providers:apple:SignedClientSecret:PrivateKey", pem ?? Pem },
    };

    private OidcProvider Apple(string? pem = null) => OidcProviders.Find(Config(AppleSettings(pem)), "apple")!;

    private static JsonElement Part(string jwt, int index) =>
        JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.Split('.')[index])).RootElement;

    [Fact]
    public void The_client_secret_is_an_es256_jwt_from_the_team_for_the_client_and_the_provider()
    {
        var provider = Apple();
        provider.Should().NotBeNull("a signed secret stands in for ClientSecret");
        provider.FormPost.Should().BeTrue();

        var secret = new OidcClientSecrets { Now = () => _now }.For(provider);

        var parts = secret.Split('.');
        parts.Should().HaveCount(3);
        var header = Part(secret, 0);
        header.GetProperty("alg").GetString().Should().Be("ES256");
        header.GetProperty("kid").GetString().Should().Be(KeyId);

        var claims = Part(secret, 1);
        claims.GetProperty("iss").GetString().Should().Be(TeamId);
        claims.GetProperty("sub").GetString().Should().Be("com.example.web");
        claims.GetProperty("aud").GetString().Should().Be(OidcStubProvider.AppleAuthority,
            "with no Audience set, the provider's issuer is the audience, which is what Apple asks for");
        var issuedAt = claims.GetProperty("iat").GetInt64();
        var expires = claims.GetProperty("exp").GetInt64();
        issuedAt.Should().Be(_now.ToUnixTimeSeconds());
        (expires - issuedAt).Should().Be((long)OidcClientSecrets.Lifetime.TotalSeconds);
        (expires - issuedAt).Should().BeLessThan(15_777_000, "Apple refuses a secret that lives longer than six months");

        // Verified with the public half only, by hand, so a shared mistake in a library cannot pass.
        using var publicKey = ECDsa.Create(_key.ExportParameters(includePrivateParameters: false));
        publicKey.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlEncoder.DecodeBytes(parts[2]), HashAlgorithmName.SHA256)
            .Should().BeTrue("the signature is r and s side by side over the header and payload, as JWS has it");
        secret.Should().NotContain("PRIVATE KEY");
    }

    [Fact]
    public void The_client_secret_is_reused_until_a_day_before_it_expires_and_then_made_again()
    {
        var provider = Apple();
        var start = _now;
        var secrets = new OidcClientSecrets { Now = () => _now };

        var first = secrets.For(provider);
        _now = start + OidcClientSecrets.Lifetime - OidcClientSecrets.RenewBefore - TimeSpan.FromMinutes(1);
        secrets.For(provider).Should().Be(first, "the key is used about once a month, not on every sign-in");

        _now = start + OidcClientSecrets.Lifetime - OidcClientSecrets.RenewBefore + TimeSpan.FromMinutes(1);
        var renewed = secrets.For(provider);

        renewed.Should().NotBe(first, "a secret close to its expiry is not sent");
        Part(renewed, 1).GetProperty("iat").GetInt64().Should().Be(_now.ToUnixTimeSeconds());
        secrets.For(provider).Should().Be(renewed);
    }

    [Fact]
    public void A_changed_key_gets_a_new_secret_at_once()
    {
        var secrets = new OidcClientSecrets { Now = () => _now };
        var first = secrets.For(Apple());

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var second = secrets.For(Apple(other.ExportPkcs8PrivateKeyPem()));

        second.Should().NotBe(first, "a rotated key must not wait a month for the cached secret to age out");
    }

    [Fact]
    public void A_key_written_with_escaped_line_breaks_is_read_as_the_key()
    {
        var provider = Apple(Pem.Replace("\n", "\\n"));

        provider.Should().NotBeNull("an environment variable cannot hold a line break");
        provider.SignedSecret.Should().NotBeNull();
    }

    [Fact]
    public void A_provider_without_a_signed_secret_sends_its_configured_one()
    {
        var provider = OidcProviders.Find(Config(new()
        {
            { "Oidc:Providers:stub:Authority", OidcStubProvider.Authority },
            { "Oidc:Providers:stub:ClientId", OidcStubProvider.ClientId },
            { "Oidc:Providers:stub:ClientSecret", OidcStubProvider.ClientSecret },
        }), "stub")!;

        provider.SignedSecret.Should().BeNull();
        provider.FormPost.Should().BeFalse("a redirect back is the default");
        new OidcClientSecrets().For(provider).Should().Be(OidcStubProvider.ClientSecret);
    }

    public enum BadKey
    {
        NotPem,
        P384,
        Rsa,
        NoTeam,
    }

    [Theory]
    [InlineData(BadKey.NotPem)]
    [InlineData(BadKey.P384)]
    [InlineData(BadKey.Rsa)]
    [InlineData(BadKey.NoTeam)]
    public void A_signed_secret_that_cannot_be_used_turns_the_provider_off_and_says_why(BadKey bad)
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        var settings = AppleSettings(bad switch
        {
            BadKey.NotPem => "not a key",
            BadKey.P384 => p384.ExportPkcs8PrivateKeyPem(),
            BadKey.Rsa => rsa.ExportPkcs8PrivateKeyPem(),
            _ => null,
        });
        if (bad == BadKey.NoTeam)
        {
            settings.Remove("Oidc:Providers:apple:SignedClientSecret:TeamId");
        }

        // A ClientSecret as well, to show the provider does not quietly fall back to it.
        settings["Oidc:Providers:apple:ClientSecret"] = "fallback";
        var config = Config(settings);

        OidcProviders.Find(config, "apple").Should().BeNull();
        var problems = OidcProviders.Problems(config);
        problems.Should().HaveCount(1);
        problems[0].Should().Contain("SignedClientSecret").And.NotContain("PRIVATE KEY");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task The_text_true_counts_as_verified_only_for_a_provider_configured_to_take_it(bool configured, bool verified)
    {
        using var rsa = RSA.Create(2048);
        var provider = new OidcProvider(
            "apple", "Apple", OidcStubProvider.AppleAuthority, OidcStubProvider.AppleAuthority,
            OidcStubProvider.ClientId, "unused", "openid email", OidcProviders.DefaultEmailVerifiedClaim)
        {
            EmailVerifiedMayBeText = configured,
        };
        var claims = OidcTestTokens.Claims("nonce-for-this-request", "apple-subject", "person@example.com");
        claims["iss"] = OidcStubProvider.AppleAuthority;
        claims["email_verified"] = "true";
        var keys = new JsonWebKeySet(OidcStubProvider.Jwks(rsa, OidcStubProvider.KeyId)).Keys.ToList();

        var (identity, refusal) = await OidcIdToken.ValidateAsync(
            OidcTestTokens.Rs256(rsa, claims), provider, keys, "nonce-for-this-request");

        refusal.Should().Be(OidcRefusal.None);
        identity!.EmailVerified.Should().Be(verified);
    }
}
