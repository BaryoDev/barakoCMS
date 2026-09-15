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

    /// <summary>
    /// Encrypts every credential-named parameter on every action of a definition that arrived in a
    /// request, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every action type, not only Webhook (issue #526), and every name
    /// <see cref="IsSensitiveParameterName"/> matches, not only <see cref="SecretParameter"/> (issue
    /// #765). The API already hides all of those names on read and the action metadata reports them
    /// as secret, so encrypting only one of them showed the rest as protected while they sat in clear.
    /// </para>
    /// <para>
    /// Every non-blank value is encrypted, whatever it looks like. A request carries what somebody
    /// typed, and the API never hands ciphertext back to type in, so nothing here is already an
    /// envelope. Skipping values that looked like one is what left a base64 or hex API key in clear.
    /// Stored definitions go through <see cref="MigrateStoredCredentials"/> instead, which does have
    /// to tell the two apart.
    /// </para>
    /// <para>
    /// Only <see cref="SecretParameter"/> is trimmed, as it always was. Any other credential is kept
    /// exactly as typed, because a password with a leading space is a different password.
    /// </para>
    /// <para>
    /// Unprotecting is split. <see cref="SecretParameter"/> reaches the action as ciphertext, as it
    /// always has, and the action decrypts it (Webhook does, in <see cref="WebhookAction"/>). Every
    /// other credential name is decrypted before the action sees it, by
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
                var value = action.Parameters[name];

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (name != SecretParameter) continue;

                    action.Parameters.Remove(name);
                    changed = true;
                    continue;
                }

                action.Parameters[name] = protector.Protect(name == SecretParameter ? value.Trim() : value);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Brings the credential-named parameters of a stored definition up to the prefixed envelope, in
    /// place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value with the prefix is left alone. One without it is one of three things, told apart by
    /// trying the current key: an envelope written before the prefix existed (it decrypts, and gains
    /// the prefix with its ciphertext unchanged), a value stored in clear (it does not decrypt, and is
    /// encrypted), or a <see cref="SecretParameter"/> that has the envelope's shape and will not
    /// decrypt.
    /// </para>
    /// <para>
    /// That last one is left untouched and reported through <paramref name="undecryptableSecret"/>.
    /// Secret has been encrypted on save since #524, so an envelope-shaped Secret that will not
    /// decrypt is most likely a changed Secrets:Key, and encrypting the old ciphertext as if it were
    /// the secret would sign every delivery with garbage instead of refusing with a message that says
    /// to enter it again. The other names were never encrypted before #765, so for them clear is the
    /// likely reading.
    /// </para>
    /// <para>
    /// Safe to run again and on several instances at once: every write produces a prefixed envelope
    /// of the same plaintext, and a prefixed value is never touched.
    /// </para>
    /// </remarks>
    /// <param name="undecryptableSecret">Called with the action's index and the parameter name, never the value.</param>
    /// <returns>True when any parameter was changed.</returns>
    public static bool MigrateStoredCredentials(
        WorkflowDefinition workflow, ISecretProtector protector, Action<int, string>? undecryptableSecret = null)
    {
        var changed = false;

        for (var index = 0; index < workflow.Actions.Count; index++)
        {
            var action = workflow.Actions[index];

            foreach (var name in action.Parameters.Keys.Where(IsSensitiveParameterName).ToList())
            {
                var value = action.Parameters[name];

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (name != SecretParameter) continue;

                    action.Parameters.Remove(name);
                    changed = true;
                    continue;
                }

                if (LooksProtected(value)) continue;

                if (protector.Unprotect(value) is not null)
                {
                    action.Parameters[name] = AesGcmEnvelope.VersionPrefix + value;
                    changed = true;
                    continue;
                }

                if (name == SecretParameter && AesGcmEnvelope.IsWellFormed(value))
                {
                    undecryptableSecret?.Invoke(index, name);
                    continue;
                }

                action.Parameters[name] = protector.Protect(name == SecretParameter ? value.Trim() : value);
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
    /// A prefixed value that will not decrypt is a changed Secrets:Key, and the error says so by
    /// parameter name, never by value. A value without the prefix is tried too, since an envelope
    /// written before the prefix existed can still be on a run queued before the startup migration
    /// reached its workflow; when it does not decrypt it is a value in clear from before #765, which
    /// worked before the upgrade and so passes through as it is.
    /// </remarks>
    public static (Dictionary<string, string> Parameters, string? Error) UnprotectCredentials(
        IReadOnlyDictionary<string, string> parameters, ISecretProtector protector)
    {
        var copy = new Dictionary<string, string>(parameters.Count);

        foreach (var (name, value) in parameters)
        {
            if (name == SecretParameter || !IsSensitiveParameterName(name) || string.IsNullOrEmpty(value))
            {
                copy[name] = value;
                continue;
            }

            var plaintext = protector.Unprotect(value);
            if (plaintext is null && LooksProtected(value))
            {
                return (copy, $"The {name} parameter could not be decrypted (Secrets:Key changed?). Enter it again on the workflow.");
            }

            copy[name] = plaintext ?? value;
        }

        return (copy, null);
    }

    public static bool HasSecret(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(SecretParameter, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>Whether a stored value carries the envelope prefix <see cref="ISecretProtector"/> writes.</summary>
    public static bool LooksProtected(string storedValue) => AesGcmEnvelope.HasVersionPrefix(storedValue);

    /// <summary>
    /// Whether a stored Secret that will not decrypt could ever have been ciphertext, as opposed to
    /// plaintext saved before this action's secret was protected.
    /// </summary>
    /// <remarks>
    /// A Webhook action saved before #524 (or a custom action saved before #526) can hold a Secret
    /// parameter that was never encrypted. <see cref="ISecretProtector.Unprotect"/> returns null both
    /// for that case and for one that will not decrypt under the current key, and the two need
    /// different messages: an operator retyping a rotated secret is not the same fix as recreating a
    /// workflow that predates encryption. The prefix settles it for a value written since; for one
    /// written before the prefix, the envelope's shape is the best evidence there is.
    /// </remarks>
    public static bool CouldBeCiphertext(string storedValue) =>
        LooksProtected(storedValue) || AesGcmEnvelope.IsWellFormed(storedValue);

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
