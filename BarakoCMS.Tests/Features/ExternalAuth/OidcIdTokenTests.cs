using System.Security.Cryptography;
using System.Text;
using BarakoCMS.ExternalAuth;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>
/// What an id token has to prove before anybody is signed in on the strength of it (#786).
/// </summary>
/// <remarks>
/// Every refusal names its reason, and each test asserts the reason and not only that the token was
/// refused. A token with two things wrong, or a validator that refuses everything, would otherwise
/// pass a test written for one of them. The first test is the control: the same claims, untouched,
/// are accepted.
/// </remarks>
public class OidcIdTokenTests : IDisposable
{
    private const string Nonce = "nonce-for-this-request";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static OidcProvider Provider(string? issuer = null, string verifiedClaim = "email_verified") => new(
        "stub", "Stub ID", OidcStubProvider.Authority, issuer ?? OidcStubProvider.Authority,
        OidcStubProvider.ClientId, OidcStubProvider.ClientSecret, "openid email profile", verifiedClaim);

    private IReadOnlyList<JsonWebKey> Keys(string? keyIssuer = null) =>
        new JsonWebKeySet(OidcStubProvider.Jwks(_key, OidcStubProvider.KeyId, keyIssuer)).Keys.ToList();

    private static Dictionary<string, object?> Claims(Action<Dictionary<string, object?>>? edit = null)
    {
        var claims = OidcTestTokens.Claims(Nonce, "subject-1", "Person@Example.com");
        edit?.Invoke(claims);
        return claims;
    }

    private async Task<(OidcIdentity? Identity, OidcRefusal Refusal)> ValidateAsync(
        string token, OidcProvider? provider = null, IReadOnlyList<JsonWebKey>? keys = null) =>
        await OidcIdToken.ValidateAsync(token, provider ?? Provider(), keys ?? Keys(), Nonce);

    private async Task ShouldBeRefusedAsync(string token, OidcRefusal reason, OidcProvider? provider = null, IReadOnlyList<JsonWebKey>? keys = null)
    {
        var (identity, refusal) = await ValidateAsync(token, provider, keys);
        identity.Should().BeNull();
        refusal.Should().Be(reason);
    }

    [Fact]
    public async Task A_token_the_provider_issued_to_this_client_for_this_request_is_accepted()
    {
        var (identity, refusal) = await ValidateAsync(OidcTestTokens.Rs256(_key, Claims()));

        refusal.Should().Be(OidcRefusal.None);
        identity.Should().NotBeNull();
        identity!.Issuer.Should().Be(OidcStubProvider.Authority);
        identity.Subject.Should().Be("subject-1");
        identity.Email.Should().Be("person@example.com", "the address is lowercased, as the user index stores it");
        identity.EmailVerified.Should().BeTrue();
        identity.Name.Should().Be("Stub Person");
    }

    [Fact]
    public async Task An_audience_list_that_contains_the_client_is_accepted()
    {
        var token = OidcTestTokens.Rs256(_key, Claims(c =>
        {
            c["aud"] = new[] { "another-client", OidcStubProvider.ClientId };
            c["azp"] = OidcStubProvider.ClientId;
        }));

        (await ValidateAsync(token)).Refusal.Should().Be(OidcRefusal.None);
    }

    [Fact]
    public async Task A_token_for_another_client_is_refused()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["aud"] = "another-client")), OidcRefusal.Audience);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["aud"] = OidcStubProvider.ClientId + "/")), OidcRefusal.Audience);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c.Remove("aud"))), OidcRefusal.Audience);
    }

    [Fact]
    public async Task A_token_issued_to_another_party_is_refused_even_when_this_client_is_in_the_audience()
    {
        var token = OidcTestTokens.Rs256(_key, Claims(c =>
        {
            c["aud"] = new[] { "another-client", OidcStubProvider.ClientId };
            c["azp"] = "another-client";
        }));

        await ShouldBeRefusedAsync(token, OidcRefusal.Audience);
    }

    [Theory]
    [InlineData("https://idp.test.example/")]
    [InlineData("https://IDP.test.example")]
    [InlineData("http://idp.test.example")]
    [InlineData("https://idp.test.example.evil.example")]
    public async Task An_issuer_that_is_not_exactly_the_configured_one_is_refused(string issuer)
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["iss"] = issuer)), OidcRefusal.Issuer);
    }

    [Fact]
    public async Task A_token_with_no_issuer_is_refused()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c.Remove("iss"))), OidcRefusal.Issuer);
    }

    [Fact]
    public async Task Expiry_and_not_before_are_honoured_with_a_minute_of_skew()
    {
        long At(int seconds) => DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds();

        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["exp"] = At(-600))), OidcRefusal.Lifetime);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["nbf"] = At(600))), OidcRefusal.Lifetime);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c.Remove("exp"))), OidcRefusal.Lifetime);

        (await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(c => c["exp"] = At(-20))))).Refusal
            .Should().Be(OidcRefusal.None, "twenty seconds past expiry is inside the skew two clocks are allowed");
        (await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(c => c["nbf"] = At(20))))).Refusal
            .Should().Be(OidcRefusal.None);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_the_provider_does_not_publish_is_refused()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_otherKey, Claims()), OidcRefusal.Signature);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_otherKey, Claims(), keyId: "a-key-nobody-published"), OidcRefusal.Signature);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims()), OidcRefusal.Signature, keys: Array.Empty<JsonWebKey>());
    }

    [Fact]
    public async Task A_payload_changed_after_signing_is_refused()
    {
        var parts = OidcTestTokens.Rs256(_key, Claims()).Split('.');
        var forged = OidcTestTokens.Rs256(_otherKey, Claims(c => c["sub"] = "somebody-else")).Split('.');

        await ShouldBeRefusedAsync($"{parts[0]}.{forged[1]}.{parts[2]}", OidcRefusal.Signature);
    }

    [Fact]
    public async Task An_unsigned_token_is_refused_for_its_algorithm()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Unsigned(Claims()), OidcRefusal.Algorithm);
    }

    /// <summary>
    /// Two ways to try a symmetric signature: keyed with the client secret, which the client knows,
    /// and keyed with the bytes of the public key, which everybody knows.
    /// </summary>
    [Fact]
    public async Task An_hmac_token_is_refused_for_its_algorithm_whatever_it_was_keyed_with()
    {
        var withSecret = OidcTestTokens.Hs256(Encoding.UTF8.GetBytes(OidcStubProvider.ClientSecret), Claims());
        var withPublicKey = OidcTestTokens.Hs256(_key.ExportSubjectPublicKeyInfo(), Claims());

        await ShouldBeRefusedAsync(withSecret, OidcRefusal.Algorithm);
        await ShouldBeRefusedAsync(withPublicKey, OidcRefusal.Algorithm);
    }

    [Fact]
    public async Task A_nonce_that_is_not_this_requests_is_refused()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["nonce"] = "a-nonce-from-another-request")), OidcRefusal.Nonce);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c.Remove("nonce"))), OidcRefusal.Nonce);
    }

    [Fact]
    public async Task A_token_with_no_subject_is_refused()
    {
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c.Remove("sub"))), OidcRefusal.Subject);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["sub"] = new string('s', 256))), OidcRefusal.Subject);
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Something_that_is_not_a_token_is_refused_as_malformed(string token)
    {
        await ShouldBeRefusedAsync(token, OidcRefusal.Malformed);
    }

    [Fact]
    public async Task A_token_longer_than_the_bound_is_refused_before_it_is_parsed()
    {
        var token = OidcTestTokens.Rs256(_key, Claims(c => c["padding"] = new string('x', OidcIdToken.MaxLength)));

        await ShouldBeRefusedAsync(token, OidcRefusal.Malformed);
    }

    [Fact]
    public async Task The_email_is_verified_only_when_the_configured_claim_says_so()
    {
        async Task<bool> VerifiedAsync(Action<Dictionary<string, object?>> edit, string claim = "email_verified")
        {
            var (identity, refusal) = await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(edit)), Provider(verifiedClaim: claim));
            refusal.Should().Be(OidcRefusal.None);
            return identity!.EmailVerified;
        }

        (await VerifiedAsync(c => c["email_verified"] = true)).Should().BeTrue();
        (await VerifiedAsync(c => c["email_verified"] = "true")).Should().BeFalse(
            "only the JSON boolean counts: text is what a profile attribute a user typed looks like");
        (await VerifiedAsync(c => c["email_verified"] = 1)).Should().BeFalse();
        (await VerifiedAsync(c => c["xms_edov"] = "true", claim: "xms_edov")).Should().BeFalse("the same rule for a configured claim");
        (await VerifiedAsync(c => c["email_verified"] = false)).Should().BeFalse();
        (await VerifiedAsync(c => c["email_verified"] = "yes")).Should().BeFalse();
        (await VerifiedAsync(c => c.Remove("email_verified"))).Should().BeFalse("absent is no assertion at all");
        (await VerifiedAsync(c => c.Remove("email"))).Should().BeFalse("a flag with no address verifies nothing");
        (await VerifiedAsync(c => c["xms_edov"] = true, claim: "xms_edov")).Should().BeTrue();
        (await VerifiedAsync(c => { }, claim: "xms_edov")).Should().BeFalse(
            "with another claim configured, the standard one is not consulted");
    }

    // ---- a template issuer, as Microsoft's multi-directory endpoints publish ----

    private const string Template = OidcStubProvider.SharedIssuerTemplate;

    private static Dictionary<string, object?> DirectoryClaims(string directory, Action<Dictionary<string, object?>>? edit = null) =>
        Claims(c =>
        {
            c["iss"] = Template.Replace("{tenantid}", directory);
            c["tid"] = directory;
            edit?.Invoke(c);
        });

    [Fact]
    public async Task A_template_issuer_accepts_the_issuer_of_the_directory_the_token_names()
    {
        var directory = Guid.NewGuid().ToString();

        var (identity, refusal) = await ValidateAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory)), Provider(Template), Keys(Template));

        refusal.Should().Be(OidcRefusal.None);
        identity!.Issuer.Should().Be($"https://idp.test.example/{directory}/v2.0",
            "the directory's own issuer is what the account is keyed by");
    }

    [Fact]
    public async Task A_template_issuer_refuses_an_issuer_that_is_not_the_named_directorys()
    {
        var directory = Guid.NewGuid().ToString();
        var another = Guid.NewGuid().ToString();
        var provider = Provider(Template);

        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory, c => c["iss"] = Template.Replace("{tenantid}", another))),
            OidcRefusal.Issuer, provider, Keys(Template));
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory, c => c.Remove("tid"))),
            OidcRefusal.Issuer, provider, Keys(Template));
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims("common")),
            OidcRefusal.Issuer, provider, Keys(Template));
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory, c => c["iss"] = Template)),
            OidcRefusal.Issuer, provider, Keys(Template));
    }

    [Fact]
    public async Task A_key_published_for_one_directory_does_not_vouch_for_another()
    {
        var directory = Guid.NewGuid().ToString();
        var keyOwner = Guid.NewGuid().ToString();
        var token = OidcTestTokens.Rs256(_key, DirectoryClaims(directory));

        await ShouldBeRefusedAsync(token, OidcRefusal.SigningKeyIssuer, Provider(Template),
            Keys(Template.Replace("{tenantid}", keyOwner)));

        (await ValidateAsync(token, Provider(Template), Keys(Template.Replace("{tenantid}", directory)))).Refusal
            .Should().Be(OidcRefusal.None, "the same key published for the token's own directory is the control");
    }

    /// <summary>
    /// The header is written by whoever made the token. Leaving out the key id, or naming one that
    /// is not published, must not get a token verified under a key the issuer rule is then not
    /// applied to.
    /// </summary>
    [Fact]
    public async Task With_a_template_issuer_a_key_for_another_directory_does_not_vouch_whatever_key_id_the_header_carries()
    {
        var directory = Guid.NewGuid().ToString();
        var keyOwner = Guid.NewGuid().ToString();
        var provider = Provider(Template);
        var anotherDirectorysKey = Keys(Template.Replace("{tenantid}", keyOwner));

        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory), keyId: null),
            OidcRefusal.SigningKeyIssuer, provider, anotherDirectorysKey);
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory), keyId: "a-key-id-not-in-the-set"),
            OidcRefusal.SigningKeyIssuer, provider, anotherDirectorysKey);

        (await ValidateAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory)), provider, Keys(Template.Replace("{tenantid}", directory)))).Refusal
            .Should().Be(OidcRefusal.None, "the same token under the key published for its own directory, named by its id");
    }

    [Fact]
    public async Task With_a_template_issuer_a_token_must_name_its_key()
    {
        var directory = Guid.NewGuid().ToString();

        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, DirectoryClaims(directory), keyId: null),
            OidcRefusal.SigningKeyIssuer, Provider(Template), Keys(Template.Replace("{tenantid}", directory)));
    }

    private IReadOnlyList<JsonWebKey> TwoKeys(string otherKeyId) =>
        Keys().Concat(new JsonWebKeySet(OidcStubProvider.Jwks(_otherKey, otherKeyId)).Keys).ToList();

    [Fact]
    public async Task A_token_with_no_key_id_is_accepted_only_when_there_is_one_key_to_mean()
    {
        var token = OidcTestTokens.Rs256(_key, Claims(), keyId: null);

        (await ValidateAsync(token)).Refusal.Should().Be(OidcRefusal.None, "a provider with one key has nothing to choose between");
        await ShouldBeRefusedAsync(token, OidcRefusal.Signature, keys: TwoKeys("another-key"));
    }

    [Fact]
    public async Task A_key_id_has_to_name_exactly_one_published_key()
    {
        var keys = TwoKeys("another-key");

        (await ValidateAsync(OidcTestTokens.Rs256(_key, Claims()), keys: keys)).Refusal.Should().Be(OidcRefusal.None);
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, Claims(), keyId: "another-key"), OidcRefusal.Signature, keys: keys);
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, Claims(), keyId: "a-key-id-not-in-the-set"), OidcRefusal.Signature, keys: keys);
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, Claims()), OidcRefusal.Signature, keys: TwoKeys(OidcStubProvider.KeyId));
    }

    [Fact]
    public async Task With_several_audiences_the_token_has_to_name_this_client_as_the_party_it_was_issued_to()
    {
        var several = new[] { "another-client", OidcStubProvider.ClientId };

        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["aud"] = several)), OidcRefusal.Audience);
        await ShouldBeRefusedAsync(
            OidcTestTokens.Rs256(_key, Claims(c => { c["aud"] = several; c["azp"] = 5; })), OidcRefusal.Audience);
        await ShouldBeRefusedAsync(OidcTestTokens.Rs256(_key, Claims(c => c["azp"] = 5)), OidcRefusal.Audience);

        (await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(c => { c["aud"] = several; c["azp"] = OidcStubProvider.ClientId; }))))
            .Refusal.Should().Be(OidcRefusal.None);
        (await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(c => c["aud"] = new[] { OidcStubProvider.ClientId }))))
            .Refusal.Should().Be(OidcRefusal.None, "one audience in a list is still one audience, and needs no azp");
    }

    [Theory]
    [InlineData("http://idp.test.example/p.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("//cdn.example/p.png")]
    [InlineData("p.png")]
    public async Task A_picture_that_is_not_an_https_address_is_not_kept(string picture)
    {
        var (identity, refusal) = await ValidateAsync(OidcTestTokens.Rs256(_key, Claims(c => c["picture"] = picture)));

        refusal.Should().Be(OidcRefusal.None, "a bad picture is dropped, it does not fail the sign-in");
        identity!.Picture.Should().BeNull();
    }

    [Fact]
    public async Task An_https_picture_is_kept()
    {
        var (identity, _) = await ValidateAsync(OidcTestTokens.Rs256(_key, Claims()));

        identity!.Picture.Should().Be("https://idp.test.example/p.png");
    }
}
