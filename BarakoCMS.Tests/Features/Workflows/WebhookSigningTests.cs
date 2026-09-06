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
}
