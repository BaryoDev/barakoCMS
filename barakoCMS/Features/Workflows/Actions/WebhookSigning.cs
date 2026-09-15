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

    /// <summary>Encrypts every credential-named parameter on every action, in place.</summary>
    /// <remarks>
    /// <para>
    /// Every action type, not only Webhook (issue #526), and every name
    /// <see cref="IsSensitiveParameterName"/> matches, not only <see cref="SecretParameter"/> (issue
    /// #765). The API already hides all of those names on read and the action metadata reports them
    /// as secret, so encrypting only one of them showed the rest as protected while they sat in clear.
    /// </para>
    /// <para>
    /// A value already shaped like an envelope is left alone, which is what lets the startup
    /// migration run this over stored workflows more than once.
    /// </para>
    /// <para>
    /// Unprotecting is split. <see cref="SecretParameter"/> reaches the action as ciphertext, as it
    /// always has, and the action decrypts it (Webhook does, in <see cref="WebhookAction"/>). Every
    /// other credential name is decrypted by the runner before the action sees it, by
    /// <see cref="UnprotectCredentials"/>, so a custom action that read its ApiKey as plaintext before
    /// this still does.
    /// </para>
    /// </remarks>
    /// <returns>True when any parameter was changed.</returns>
    public static bool ProtectSecrets(WorkflowDefinition workflow, ISecretProtector protector)
    {
        var changed = false;

        foreach (var action in workflow.Actions)
        {
            foreach (var name in action.Parameters.Keys.Where(IsSensitiveParameterName).ToList())
            {
                var trimmed = action.Parameters[name]?.Trim() ?? string.Empty;

                if (trimmed.Length == 0)
                {
                    if (name != SecretParameter) continue;

                    action.Parameters.Remove(name);
                    changed = true;
                    continue;
                }

                if (LooksProtected(trimmed)) continue;

                action.Parameters[name] = protector.Protect(trimmed);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// A copy of the parameters with every credential other than <see cref="SecretParameter"/>
    /// decrypted, for handing to an action.
    /// </summary>
    /// <remarks>
    /// A value that is not shaped like an envelope passes through as it is. That is a workflow saved
    /// before #765 that the startup migration has not reached yet, and it worked in clear before the
    /// upgrade, so it keeps working. A value that is shaped right and still will not decrypt is a
    /// changed Secrets:Key, and the error says so by parameter name, never by value.
    /// </remarks>
    public static (Dictionary<string, string> Parameters, string? Error) UnprotectCredentials(
        IReadOnlyDictionary<string, string> parameters, ISecretProtector protector)
    {
        var copy = new Dictionary<string, string>(parameters.Count);

        foreach (var (name, value) in parameters)
        {
            if (name == SecretParameter || !IsSensitiveParameterName(name) || !LooksProtected(value))
            {
                copy[name] = value;
                continue;
            }

            var plaintext = protector.Unprotect(value);
            if (plaintext is null)
            {
                return (copy, $"The {name} parameter could not be decrypted (Secrets:Key changed?). Enter it again on the workflow.");
            }

            copy[name] = plaintext;
        }

        return (copy, null);
    }

    public static bool HasSecret(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(SecretParameter, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Whether a stored Secret value is shaped like something <see cref="ProtectSecrets"/> produced,
    /// as opposed to plaintext saved before this action's secret was protected.
    /// </summary>
    /// <remarks>
    /// A Webhook action saved before #524 (or a custom action saved before #526) can hold a Secret
    /// parameter that was never encrypted. <see cref="ISecretProtector.Unprotect"/> returns null both
    /// for that case and for one that is shaped right but will not decrypt under the current key, and
    /// the two need different messages: an operator retyping a rotated secret is not the same fix as
    /// recreating a workflow that predates encryption.
    /// </remarks>
    public static bool LooksProtected(string storedValue) => AesGcmEnvelope.IsWellFormed(storedValue);

    /// <summary>
    /// Parameter names whose value is a credential, and so must never be stored on a run record or
    /// returned by the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SecretParameter"/> is the one this codebase encrypts, but it is not the only name a
    /// credential arrives under. A workflow action's parameters are free-form, so a connector call
    /// configured by hand carries whatever the third party calls it: a Password, a Token, an ApiKey.
    /// Redacting only the exact string "Secret" left every one of those in the execution log, which
    /// is served over the API to anyone who can read workflow runs.
    /// </para>
    /// <para>
    /// Matching is on a substring, case-insensitively, and deliberately errs towards redacting. A
    /// parameter called <c>TokenUrl</c> is not a secret and will still be hidden here, which costs an
    /// operator one lookup in the workflow definition. The other way round costs a credential.
    /// </para>
    /// </remarks>
    private static readonly string[] SensitiveNameParts =
    [
        "secret", "password", "passwd", "pwd", "token", "apikey", "api_key",
        "credential", "privatekey", "private_key", "accesskey", "access_key",
    ];

    /// <summary>Whether a parameter name reads as credential-bearing.</summary>
    public static bool IsSensitiveParameterName(string name) =>
        !string.IsNullOrEmpty(name)
        && SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>A copy of the parameters with credential values left out, for anything stored or shown.</summary>
    public static Dictionary<string, string> WithoutSecret(IReadOnlyDictionary<string, string> parameters)
    {
        var copy = new Dictionary<string, string>(parameters.Count);
        foreach (var (key, value) in parameters)
        {
            if (IsSensitiveParameterName(key)) continue;
            copy[key] = value;
        }

        return copy;
    }
}
