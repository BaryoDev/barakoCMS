using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests;

/// <summary>
/// The stored format of an encrypted secret, pinned against vectors computed outside this codebase.
/// </summary>
/// <remarks>
/// Both protectors read values that are already in databases, so the format is not an implementation
/// detail: changing the layout, the nonce or tag length, or the key derivation makes every secret
/// already stored undecryptable, and for MFA that is every second factor in the deployment.
///
/// A round trip through this code cannot catch that. Protect then Unprotect agrees with itself
/// whatever the format is, which is exactly the test that passes while the data dies. The vectors
/// below were produced by an independent AES-GCM implementation from the documented inputs, so they
/// fail if the layout moves.
///
/// Wire format: base64(nonce[12] | tag[16] | ciphertext), key = SHA-256 of the configured material.
/// </remarks>
public class SecretProtectorTests
{
    private const string KeyMaterial = "test-super-secret-key-that-is-at-least-32-chars-long";

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    [Fact]
    public void An_mfa_secret_stored_under_the_old_format_still_decrypts()
    {
        var protector = new MfaSecretProtector(Config(("JWT:Key", KeyMaterial)));

        protector.Unprotect("AAECAwQFBgcICQoL65d1sFkCGeYLhtQcgED2lRI4CDSFdUnUknpomHKTBc0=")
            .Should().Be("JBSWY3DPEHPK3PXP",
                "this is a second factor already in somebody's database, and the cipher moved out to "
              + "a shared class when email credentials needed the same treatment");
    }

    [Fact]
    public void A_stored_secret_decrypts_from_the_same_format()
    {
        var protector = new SecretProtector(Config(("JWT:Key", KeyMaterial)));

        protector.Unprotect("AAECAwQFBgcICQoLSgh29ZUqH88j5UAQKCRBUiofBAKDKmTysm1Lti+nNPM8xVMnX+0wKug7yB4=")
            .Should().Be("re_a_live_sending_credential");
    }

    [Fact]
    public void What_it_encrypts_it_decrypts()
    {
        var protector = new SecretProtector(Config(("JWT:Key", KeyMaterial)));

        protector.Unprotect(protector.Protect("re_round_trip")).Should().Be("re_round_trip");
    }

    /// <summary>
    /// Encrypting the same value twice gives different ciphertext.
    /// </summary>
    /// <remarks>
    /// The nonce is random per call, so equal plaintexts do not produce equal rows. Without it, two
    /// deployments sharing key material could tell they held the same credential by comparing
    /// database dumps, and the endpoint could not compare ciphertexts to decide whether anything
    /// changed, which is why it compares on whether a key is present instead.
    /// </remarks>
    [Fact]
    public void The_same_secret_encrypts_differently_each_time()
    {
        var protector = new SecretProtector(Config(("JWT:Key", KeyMaterial)));

        protector.Protect("same").Should().NotBe(protector.Protect("same"));
    }

    /// <summary>
    /// A value encrypted under different key material comes back null rather than throwing.
    /// </summary>
    /// <remarks>
    /// This is the rotation case, and it is reachable rather than a bug: an operator changes
    /// Secrets:Key and every stored credential stops being readable. Null lets the caller answer as
    /// it would for a secret that was never set, which is to fall back and say it needs entering
    /// again. An exception here surfaces as a stack trace from inside a provider mid-send.
    /// </remarks>
    [Fact]
    public void A_secret_encrypted_under_other_key_material_comes_back_null()
    {
        var written = new SecretProtector(Config(("JWT:Key", KeyMaterial))).Protect("re_secret");
        var rotated = new SecretProtector(Config(("JWT:Key", "a-completely-different-32-char-key-value")));

        rotated.Unprotect(written).Should().BeNull();
    }

    /// <summary>
    /// Secrets:Key wins over JWT:Key, so the two can be rotated apart.
    /// </summary>
    [Fact]
    public void Secrets_key_is_preferred_over_the_jwt_key()
    {
        var explicitly = new SecretProtector(Config(("Secrets:Key", "the-dedicated-secrets-key-material"), ("JWT:Key", KeyMaterial)));
        var derived = new SecretProtector(Config(("JWT:Key", "the-dedicated-secrets-key-material")));

        derived.Unprotect(explicitly.Protect("re_secret")).Should().Be("re_secret",
            "both derive from the same material, so one reads what the other wrote");

        var fromJwt = new SecretProtector(Config(("JWT:Key", KeyMaterial)));
        fromJwt.Unprotect(explicitly.Protect("re_secret")).Should().BeNull(
            "and Secrets:Key was in force, so the JWT key is not what it was encrypted under");
    }
}
