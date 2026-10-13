using System.Security.Cryptography;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #900: stored secrets live under a key ring, so a key can be rotated without losing what was
/// written under the one before it.
/// </summary>
public class SecretKeyRingTests
{
    private const string KeyA = "key-ring-material-a-that-is-at-least-32-chars";
    private const string KeyB = "key-ring-material-b-that-is-at-least-32-chars";
    private const string JwtKey = "the-jwt-signing-key-that-is-at-least-32-chars";
    private const string LegacyKey = "the-old-single-secrets-key-at-least-32-chars";

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static IConfiguration RingA(string section) =>
        Config(($"{section}:Keys:a", KeyA), ($"{section}:CurrentKey", "a"));

    private static IConfiguration RingAThenB(string section) =>
        Config(($"{section}:Keys:a", KeyA), ($"{section}:Keys:b", KeyB), ($"{section}:CurrentKey", "b"));

    [Fact]
    public void A_secret_written_under_key_a_still_decrypts_after_key_b_becomes_current()
    {
        var written = new SecretProtector(RingA("Secrets")).Protect("re_live_credential");

        var rotated = new SecretProtector(RingAThenB("Secrets"));

        rotated.Unprotect(written).Should().Be("re_live_credential");
        rotated.Protect("re_new").Should().StartWith("enc:v1:b:", "new writes use the current key and name it");
    }

    [Fact]
    public void A_value_names_the_key_that_wrote_it()
    {
        var written = new SecretProtector(RingA("Secrets")).Protect("re_live_credential");

        written.Should().StartWith("enc:v1:a:");
        new SecretProtector(Config(("Secrets:Keys:b", KeyB))).Unprotect(written)
            .Should().BeNull("key a is not in this ring, and b must not be tried for a value that names a");
    }

    [Fact]
    public void A_value_with_no_key_id_still_reads_with_the_single_key_once_a_ring_is_configured()
    {
        var before = new SecretProtector(Config(("Secrets:Key", LegacyKey))).Protect("re_from_before");
        before.Should().NotStartWith("enc:v1:a:");

        var ring = new SecretProtector(Config(
            ("Secrets:Key", LegacyKey), ("Secrets:Keys:a", KeyA), ("Secrets:CurrentKey", "a")));

        ring.Unprotect(before).Should().Be("re_from_before");
    }

    [Fact]
    public void A_value_written_under_the_jwt_fallback_still_reads_but_the_jwt_key_writes_nothing_new()
    {
        var before = new SecretProtector(Config(("JWT:Key", JwtKey))).Protect("re_from_jwt");

        var ring = new SecretProtector(Config(("JWT:Key", JwtKey), ("Secrets:Keys:a", KeyA)));

        ring.Unprotect(before).Should().Be("re_from_jwt");
        var next = ring.Protect("re_new");
        next.Should().StartWith("enc:v1:a:");
        new SecretProtector(Config(("JWT:Key", JwtKey))).Unprotect(next)
            .Should().BeNull("with a ring configured the JWT key no longer encrypts stored secrets");
    }

    [Fact]
    public void Without_a_ring_a_secret_is_written_the_way_it_always_was()
    {
        var protector = new SecretProtector(Config(("JWT:Key", JwtKey)));

        var written = protector.Protect("re_plain");

        written.Should().StartWith("enc:v1:");
        AesGcmEnvelope.IsWellFormed(written["enc:v1:".Length..]).Should().BeTrue(
            "a deployment that never configures a ring stores exactly what an older build can read");
    }

    [Fact]
    public void A_ring_with_two_keys_and_no_current_one_is_refused()
    {
        var act = () => new SecretProtector(Config(("Secrets:Keys:a", KeyA), ("Secrets:Keys:b", KeyB)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Secrets:CurrentKey*");
    }

    [Fact]
    public void A_current_key_that_is_not_in_the_ring_is_refused()
    {
        var act = () => new SecretProtector(Config(("Secrets:Keys:a", KeyA), ("Secrets:CurrentKey", "b")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'b'*");
    }

    [Fact]
    public void A_current_key_with_no_usable_key_in_the_ring_is_refused_rather_than_falling_back_to_the_jwt_key()
    {
        var act = () => new SecretProtector(Config(("JWT:Key", JwtKey), ("Secrets:CurrentKey", "k2026")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Secrets:CurrentKey*");
    }

    [Fact]
    public void An_empty_ring_entry_is_refused_rather_than_skipped()
    {
        var act = () => new SecretProtector(Config(
            ("JWT:Key", JwtKey), ("Secrets:Keys:k2026", ""), ("Secrets:CurrentKey", "k2026")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Secrets:Keys:k2026*empty*");
    }

    [Fact]
    public void An_empty_ring_entry_beside_a_usable_one_is_still_refused()
    {
        var act = () => new MfaSecretProtector(Config(
            ("JWT:Key", JwtKey), ("Mfa:Keys:a", KeyA), ("Mfa:Keys:b", ""), ("Mfa:CurrentKey", "a")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mfa:Keys:b*empty*");
    }

    [Fact]
    public void A_short_ring_key_is_refused()
    {
        var act = () => new SecretProtector(Config(("Secrets:Keys:a", "too-short")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Secrets:Keys:a*");
    }

    [Fact]
    public void A_ring_key_equal_to_the_jwt_key_is_refused()
    {
        var act = () => new SecretProtector(Config(("JWT:Key", JwtKey), ("Secrets:Keys:a", JwtKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*");
    }

    [Fact]
    public void A_key_id_that_could_break_the_envelope_is_refused()
    {
        var act = () => new SecretProtector(Config(("Secrets:Keys:a.b", KeyA)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*key id*");
    }

    [Fact]
    public void An_mfa_secret_rotates_the_same_way_and_one_stored_before_the_ring_still_reads()
    {
        var before = new MfaSecretProtector(Config(("Mfa:Key", LegacyKey))).Protect("JBSWY3DPEHPK3PXP");
        var underA = new MfaSecretProtector(RingA("Mfa")).Protect("JBSWY3DPEHPK3PXQ");

        var rotated = new MfaSecretProtector(Config(
            ("Mfa:Key", LegacyKey), ("Mfa:Keys:a", KeyA), ("Mfa:Keys:b", KeyB), ("Mfa:CurrentKey", "b")));

        rotated.Unprotect(before).Should().Be("JBSWY3DPEHPK3PXP");
        rotated.Unprotect(underA).Should().Be("JBSWY3DPEHPK3PXQ");
        rotated.Protect("x").Should().StartWith("enc:v1:b:");
    }

    [Fact]
    public void An_mfa_secret_no_key_opens_still_throws()
    {
        var underA = new MfaSecretProtector(RingA("Mfa")).Protect("JBSWY3DPEHPK3PXP");

        var act = () => new MfaSecretProtector(Config(("Mfa:Keys:b", KeyB))).Unprotect(underA);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_connector_credential_rotates_and_a_ring_alone_turns_the_feature_on()
    {
        var before = new ConnectorSecretProtector(Config(("Connectors:Key", LegacyKey))).Protect("tok_before");
        var underA = new ConnectorSecretProtector(RingA("Connectors")).Protect("tok_a");

        var rotated = new ConnectorSecretProtector(Config(
            ("Connectors:Key", LegacyKey), ("Connectors:Keys:a", KeyA), ("Connectors:Keys:b", KeyB),
            ("Connectors:CurrentKey", "b")));

        rotated.IsConfigured.Should().BeTrue();
        rotated.Unprotect(before).Should().Be("tok_before");
        rotated.Unprotect(underA).Should().Be("tok_a");
        new ConnectorSecretProtector(RingA("Connectors")).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void A_workflow_credential_under_an_older_key_moves_to_the_current_key()
    {
        var underA = new SecretProtector(RingA("Secrets"));
        var rotated = new SecretProtector(RingAThenB("Secrets"));

        var workflow = new WorkflowDefinition
        {
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new()
                    {
                        ["Url"] = "https://example.test/hook",
                        ["Secret"] = underA.Protect("whsec_one"),
                        ["ApiKey"] = underA.Protect("ak_one"),
                    },
                },
            ],
        };

        WebhookSigning.MigrateStoredCredentials(workflow, rotated).Should().BeTrue();

        var parameters = workflow.Actions.Single().Parameters;
        parameters["Secret"].Should().StartWith("enc:v1:b:");
        parameters["ApiKey"].Should().StartWith("enc:v1:b:");
        rotated.Unprotect(parameters["Secret"]).Should().Be("whsec_one");
        rotated.Unprotect(parameters["ApiKey"]).Should().Be("ak_one");

        WebhookSigning.MigrateStoredCredentials(workflow, rotated).Should().BeFalse(
            "a value already under the current key is left alone");
    }

    [Fact]
    public void A_workflow_credential_no_key_opens_is_reported_and_left_as_it_is()
    {
        var underA = new SecretProtector(RingA("Secrets")).Protect("whsec_lost");
        var other = new SecretProtector(Config(("Secrets:Keys:b", KeyB)));
        var workflow = new WorkflowDefinition
        {
            Actions = [new WorkflowAction { Type = "Webhook", Parameters = new() { ["Secret"] = underA } }],
        };
        var reported = new List<string>();

        WebhookSigning.MigrateStoredCredentials(workflow, other, (_, name) => reported.Add(name)).Should().BeFalse();

        reported.Should().Equal("Secret");
        workflow.Actions.Single().Parameters["Secret"].Should().Be(underA);
    }

    [Fact]
    public void A_compensation_credential_under_an_older_key_moves_to_the_current_key()
    {
        var underA = new SecretProtector(RingA("Secrets"));
        var rotated = new SecretProtector(RingAThenB("Secrets"));
        var workflow = new WorkflowDefinition
        {
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new() { ["Url"] = "https://example.test/do" },
                    Compensate = new WorkflowCompensation
                    {
                        Type = "Webhook",
                        Parameters = new() { ["Url"] = "https://example.test/undo", ["Secret"] = underA.Protect("whsec_undo") },
                    },
                },
            ],
        };

        WebhookSigning.MigrateStoredCredentials(workflow, rotated).Should().BeTrue();

        var undo = workflow.Actions.Single().Compensate!.Parameters;
        undo["Secret"].Should().StartWith("enc:v1:b:");
        rotated.Unprotect(undo["Secret"]).Should().Be("whsec_undo");
    }

    [Fact]
    public void A_compensation_credential_no_key_opens_is_reported_and_left_as_it_is()
    {
        var underA = new SecretProtector(RingA("Secrets")).Protect("whsec_lost");
        var other = new SecretProtector(Config(("Secrets:Keys:b", KeyB)));
        var workflow = new WorkflowDefinition
        {
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new() { ["Url"] = "https://example.test/do" },
                    Compensate = new WorkflowCompensation { Type = "Webhook", Parameters = new() { ["Secret"] = underA } },
                },
            ],
        };
        var reported = new List<(int, string)>();

        WebhookSigning.MigrateStoredCredentials(workflow, other, (index, name) => reported.Add((index, name))).Should().BeFalse();

        reported.Should().Equal((0, "compensate.Secret"));
        workflow.Actions.Single().Compensate!.Parameters["Secret"].Should().Be(underA);
    }
}
