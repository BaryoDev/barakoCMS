using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #526: a Secret parameter is protected the same way regardless of which action carries it.
/// </summary>
/// <remarks>
/// <see cref="barakoCMS.Features.Workflows.WorkflowActionResponse"/> already hides the Secret
/// parameter and reports secretSet for every action type, so before this fix a custom action reusing
/// the name was shown as protected while <see cref="WebhookSigning.ProtectSecrets"/> left it in
/// clear. These tests exercise <c>ProtectSecrets</c> directly against a real
/// <see cref="SecretProtector"/> and assert on what a stored parameter actually is, not on a value
/// that never round-trips through an endpoint.
/// </remarks>
public class WebhookSigningTests
{
    private const string KeyMaterial = "test-super-secret-key-that-is-at-least-32-chars-long";

    private static ISecretProtector Protector() => new SecretProtector(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Key"] = KeyMaterial,
        }).Build());

    [Fact]
    public void A_secret_on_a_non_webhook_action_is_stored_encrypted_not_in_clear()
    {
        var plaintext = "custom_secret_" + Guid.NewGuid().ToString("N");
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction
                {
                    Type = "CustomNotifier",
                    Parameters = new Dictionary<string, string> { ["Secret"] = plaintext },
                },
            },
        };

        var protector = Protector();
        WebhookSigning.ProtectSecrets(workflow, protector);

        var stored = workflow.Actions[0].Parameters["Secret"];
        stored.Should().NotBe(plaintext, "a custom action's Secret must not be stored the way it was typed");
        protector.Unprotect(stored).Should().Be(plaintext, "it still has to be the same secret, recoverable by the deployment's own protector");
    }

    [Fact]
    public void A_webhook_secret_is_still_stored_encrypted()
    {
        var plaintext = "whsec_" + Guid.NewGuid().ToString("N");
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/x", ["Secret"] = plaintext },
                },
            },
        };

        var protector = Protector();
        WebhookSigning.ProtectSecrets(workflow, protector);

        workflow.Actions[0].Parameters["Secret"].Should().NotBe(plaintext);
    }

    [Fact]
    public void A_blank_secret_on_any_action_type_is_removed_rather_than_protected()
    {
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction
                {
                    Type = "CustomNotifier",
                    Parameters = new Dictionary<string, string> { ["Secret"] = "   " },
                },
            },
        };

        WebhookSigning.ProtectSecrets(workflow, Protector());

        workflow.Actions[0].Parameters.Should().NotContainKey("Secret");
    }

    [Fact]
    public void An_action_with_no_secret_parameter_is_left_alone()
    {
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction
                {
                    Type = "CustomNotifier",
                    Parameters = new Dictionary<string, string> { ["Channel"] = "#ops" },
                },
            },
        };

        WebhookSigning.ProtectSecrets(workflow, Protector());

        workflow.Actions[0].Parameters.Should().ContainSingle().Which.Key.Should().Be("Channel");
    }

    [Fact]
    public void A_freshly_protected_value_looks_protected()
    {
        var protector = Protector();

        WebhookSigning.LooksProtected(protector.Protect("re_secret")).Should().BeTrue();
    }

    [Fact]
    public void A_plaintext_value_saved_before_encryption_does_not_look_protected()
    {
        WebhookSigning.LooksProtected("a plaintext secret typed before #524").Should().BeFalse();
    }
    [Fact]
    public void A_base64_secret_and_a_hex_api_key_are_encrypted_rather_than_mistaken_for_ciphertext()
    {
        // Both decode to at least nonce plus tag length, the shape an envelope used to be recognised by.
        var base64Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var hexApiKey = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new Dictionary<string, string>
                    {
                        ["Url"] = "https://hooks.example.com/x", ["Secret"] = base64Secret, ["ApiKey"] = hexApiKey,
                    },
                },
            },
        };

        var protector = Protector();
        WebhookSigning.ProtectSecrets(workflow, protector);

        var parameters = workflow.Actions[0].Parameters;
        parameters["Secret"].Should().NotBe(base64Secret);
        parameters["ApiKey"].Should().NotBe(hexApiKey);
        protector.Unprotect(parameters["Secret"]).Should().Be(base64Secret);
        protector.Unprotect(parameters["ApiKey"]).Should().Be(hexApiKey);

        var (unprotected, error) = WebhookSigning.UnprotectCredentials(parameters, protector);
        error.Should().BeNull();
        unprotected["ApiKey"].Should().Be(hexApiKey);
    }

    [Fact]
    public void A_password_keeps_its_surrounding_spaces()
    {
        var workflow = new WorkflowDefinition
        {
            Actions =
            {
                new WorkflowAction { Type = "CustomNotifier", Parameters = new Dictionary<string, string> { ["Password"] = " pw " } },
            },
        };

        var protector = Protector();
        WebhookSigning.ProtectSecrets(workflow, protector);

        var (unprotected, error) = WebhookSigning.UnprotectCredentials(workflow.Actions[0].Parameters, protector);
        error.Should().BeNull();
        unprotected["Password"].Should().Be(" pw ");
    }
}
