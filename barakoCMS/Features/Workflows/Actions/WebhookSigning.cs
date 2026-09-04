using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Microsoft.Extensions.Configuration;

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

    /// <summary>
    /// Set true to let a Webhook with a Secret post to an <c>http://</c> URL. Off by default: a
    /// signed body over cleartext hands a network observer the payload and a signature it can replay
    /// inside the receiver's tolerance window. A lab talking to a loopback receiver is what it is for.
    /// </summary>
    public const string AllowInsecureSignedUrlsKey = "Webhooks:AllowInsecureSignedUrls";

    public static bool AllowsInsecureSignedUrls(IConfiguration? configuration) =>
        configuration?.GetValue(AllowInsecureSignedUrlsKey, false) ?? false;

    /// <summary>The reason a signed delivery to an http URL is refused. The validation error and the delivery row both carry it.</summary>
    public const string InsecureSignedUrlReason =
        "A Webhook with a Secret must use an https URL. Set " + AllowInsecureSignedUrlsKey + " to true to allow http.";

    /// <summary>True when the delivery would be signed over cleartext and the deployment has not opted in.</summary>
    public static bool IsInsecureSignedUrl(string? url, IReadOnlyDictionary<string, string> parameters, bool allowInsecure)
    {
        if (allowInsecure || !HasSecret(parameters)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp;
    }

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
