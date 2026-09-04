using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// How a webhook delivery is signed, and where the secret that signs it is allowed to be.
/// </summary>
/// <remarks>
/// The secret is a parameter on the action, named <see cref="SecretParameter"/>, because the
/// action's parameters are the only configuration a workflow action has. It is encrypted with
/// <see cref="ISecretProtector"/> when the workflow is saved, so the stored definition, the run
/// that copies the parameters and the execution log all hold the ciphertext. Only the action
/// decrypts it, at the moment of sending.
///
/// The signed material is <c>"{timestamp}.{body}"</c>, so a captured delivery replayed later
/// carries a timestamp the receiver can refuse. The recipe a receiver follows is in
/// <c>docs/webhooks.md</c>.
/// </remarks>
internal static class WebhookSigning
{
    public const string SecretParameter = "Secret";

    public const string SignatureHeader = "X-Barako-Signature";
    public const string TimestampHeader = "X-Barako-Timestamp";
    public const string DeliveryHeader = "X-Barako-Delivery";

    /// <summary><c>sha256=</c> followed by the lowercase hex HMAC-SHA256 of <c>"{timestamp}.{body}"</c>.</summary>
    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var material = new byte[prefix.Length + body.Length];
        prefix.CopyTo(material, 0);
        body.CopyTo(material.AsSpan(prefix.Length));

        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), material);
        return "sha256=" + Convert.ToHexStringLower(digest);
    }

    /// <summary>Encrypts the secret on every webhook action that carries one, in place.</summary>
    /// <remarks>
    /// Only the Webhook action, because it is the only one that knows to decrypt. A custom action
    /// with a parameter of the same name would otherwise be handed ciphertext it cannot use.
    /// </remarks>
    public static void ProtectSecrets(WorkflowDefinition workflow, ISecretProtector protector)
    {
        foreach (var action in workflow.Actions)
        {
            if (!string.Equals(action.Type, "Webhook", StringComparison.Ordinal)) continue;
            if (!action.Parameters.TryGetValue(SecretParameter, out var secret)) continue;

            var trimmed = secret?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                action.Parameters.Remove(SecretParameter);
                continue;
            }

            action.Parameters[SecretParameter] = protector.Protect(trimmed);
        }
    }

    public static bool HasSecret(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(SecretParameter, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>A copy of the parameters with the secret left out, for anything that is stored or shown.</summary>
    public static Dictionary<string, string> WithoutSecret(IReadOnlyDictionary<string, string> parameters)
    {
        var copy = new Dictionary<string, string>(parameters.Count);
        foreach (var (key, value) in parameters)
        {
            if (string.Equals(key, SecretParameter, StringComparison.Ordinal)) continue;
            copy[key] = value;
        }

        return copy;
    }
}
