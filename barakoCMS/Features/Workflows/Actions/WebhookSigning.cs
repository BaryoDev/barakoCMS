using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            changed |= ProtectParameters(action.Type, action.Parameters, protector);
        }

        return changed;
    }

    private static bool ProtectParameters(string? type, Dictionary<string, string> parameters, ISecretProtector protector)
    {
        var changed = false;

        foreach (var name in parameters.Keys.Where(IsSensitiveParameterName).ToList())
        {
            var value = parameters[name];

            if (string.IsNullOrWhiteSpace(value))
            {
                if (name != SecretParameter) continue;

                parameters.Remove(name);
                changed = true;
                continue;
            }

            parameters[name] = protector.Protect(name == SecretParameter ? value.Trim() : value);
            changed = true;
        }

        changed |= RewriteBranches(type, parameters, (childType, childParameters) =>
            ProtectParameters(childType, childParameters, protector));

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
    /// Every release since #524 encrypts Secret on save whatever it looks like, so on a released
    /// deployment an envelope-shaped Secret that will not decrypt is a changed Secrets:Key.
    /// Encrypting that ciphertext as if it were the secret would sign every delivery with the wrong
    /// key and stop the old key from recovering it. The cost is the other reading, a plaintext
    /// Secret that only happens to look like an envelope (a custom action from before #524, or an
    /// unreleased build between #765 and the prefix), which stays as it is until the workflow is
    /// recreated. The other names were never encrypted before #765, so for them clear is the likely
    /// reading.
    /// </para>
    /// <para>
    /// Safe to run again and on several instances at once: every write produces a prefixed envelope
    /// of the same plaintext, and a prefixed value is never touched.
    /// </para>
    /// </remarks>
    /// <param name="undecryptableSecret">Called with the action's index and the parameter name, never the value.</param>
    /// <param name="unreadableBranch">
    /// Called with the action's index and the name of a branch that was skipped because it is not
    /// readable (see <see cref="IsReadableBranch"/>), never its value.
    /// </param>
    /// <returns>True when any parameter was changed.</returns>
    public static bool MigrateStoredCredentials(
        WorkflowDefinition workflow, ISecretProtector protector, Action<int, string>? undecryptableSecret = null,
        Action<int, string>? unreadableBranch = null)
    {
        var changed = false;

        for (var index = 0; index < workflow.Actions.Count; index++)
        {
            var action = workflow.Actions[index];
            var actionIndex = index;

            // A child of a Conditional is reported against the Conditional's index, since a child
            // has no index of its own in the definition.
            changed |= MigrateParameters(
                action.Type, action.Parameters, protector,
                name => undecryptableSecret?.Invoke(actionIndex, name),
                branch => unreadableBranch?.Invoke(actionIndex, branch),
                child: false);
        }

        return changed;
    }

    private static bool MigrateParameters(
        string? type, Dictionary<string, string> parameters, ISecretProtector protector,
        Action<string> undecryptableSecret, Action<string> unreadableBranch, bool child)
    {
        var changed = false;

        foreach (var name in parameters.Keys.Where(IsSensitiveParameterName).ToList())
        {
            var value = parameters[name];

            if (string.IsNullOrWhiteSpace(value))
            {
                if (name != SecretParameter) continue;

                parameters.Remove(name);
                changed = true;
                continue;
            }

            if (LooksProtected(value))
            {
                // What follows the prefix on anything the protector wrote is base64 of at least a
                // nonce and a tag. A value with the prefix and not that shape was typed that way and
                // stored in clear. One with the shape that will not decrypt cannot be told from
                // ciphertext under another key, so it is left alone, as it always was.
                if (AesGcmEnvelope.IsWellFormed(value[AesGcmEnvelope.VersionPrefix.Length..])) continue;

                parameters[name] = protector.Protect(name == SecretParameter ? value.Trim() : value);
                changed = true;
                continue;
            }

            var decrypted = protector.Unprotect(value);
            if (decrypted is not null)
            {
                // A prefixed envelope encrypted a second time by a build that still recognised
                // ciphertext by shape, when both ran against one database. The inner envelope is
                // the credential; prefixing the outer one would hand the action ciphertext.
                var inner = LooksProtected(decrypted) && protector.Unprotect(decrypted) is not null;
                parameters[name] = inner ? decrypted : AesGcmEnvelope.VersionPrefix + value;
                changed = true;
                continue;
            }

            // Not for a child. Nothing encrypted a child's Secret before its branch was parsed, so
            // one without the prefix that will not decrypt was stored in clear, whatever its shape.
            if (!child && name == SecretParameter && AesGcmEnvelope.IsWellFormed(value))
            {
                undecryptableSecret(name);
                continue;
            }

            parameters[name] = protector.Protect(name == SecretParameter ? value.Trim() : value);
            changed = true;
        }

        changed |= RewriteBranches(
            type,
            parameters,
            (childType, childParameters) =>
                MigrateParameters(childType, childParameters, protector, undecryptableSecret, unreadableBranch, child: true),
            unreadableBranch);

        return changed;
    }

    private const string ConditionalType = "Conditional";
    private const string ChildTypeProperty = "Type";
    private const string ChildParametersProperty = "Parameters";
    private const string ChildSecretSetProperty = "SecretSet";
    private const string ChildUnreadableBranchesProperty = "UnreadableBranches";
    private static readonly string[] BranchParameters = ["ThenActions", "ElseActions"];

    // A branch is a JSON string inside a JSON document, never HTML, so the relaxed encoder keeps
    // what was typed readable instead of turning every non-ASCII character into an escape.
    private static readonly JsonSerializerOptions BranchJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool IsConditional(string? type) =>
        string.Equals(type, ConditionalType, StringComparison.OrdinalIgnoreCase);

    // Any casing, the way ActionParameters.IsResolvedByTheAction matches them, although the action
    // runs only the two exact names.
    private static bool IsBranchParameter(string name) =>
        BranchParameters.Any(branch => string.Equals(branch, name, StringComparison.OrdinalIgnoreCase));

    // Any casing, although the action reads only "Parameters": a credential under a key the action
    // ignores is still a credential somebody typed.
    private static bool IsChildParametersProperty(string name) =>
        string.Equals(name, ChildParametersProperty, StringComparison.OrdinalIgnoreCase);

    // The two names the read response writes onto a child. SecretSet reads as credential-named, and
    // a child sent back the way it was returned has to stay readable.
    private static bool IsResponseFlag(string name) =>
        string.Equals(name, ChildSecretSetProperty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ChildUnreadableBranchesProperty, StringComparison.OrdinalIgnoreCase);

    private const string UnreadableBranchShape =
        "cannot be read as a list of actions (each an object, parameter values as text, no repeated property name)";

    /// <summary>What a run says about the branch it took when that branch is not readable. Names the parameter, never its value.</summary>
    public static string UnreadableBranchReason(string branch) =>
        $"The '{branch}' parameter {UnreadableBranchShape}, so it does not run.";

    /// <summary>What a dry run says about a branch that is not readable, without knowing whether a run would take it.</summary>
    public static string UnreadableBranchWarning(string branch) =>
        $"The '{branch}' parameter {UnreadableBranchShape}. A run fails this action when it takes that branch.";

    /// <summary>
    /// Runs <paramref name="rewrite"/> over every child action a Conditional carries in its
    /// branches (the child's own string properties, then its parameters), and writes a branch back
    /// when a child changed.
    /// </summary>
    /// <remarks>
    /// A nested branch is a JSON string inside a JSON string, so each level is strictly shorter than
    /// the one holding it and the recursion is bounded by the size of the outermost value.
    /// </remarks>
    /// <param name="unreadableBranch">Called with the name of a branch that holds something and is not readable.</param>
    private static bool RewriteBranches(
        string? type, Dictionary<string, string> parameters, Func<string?, Dictionary<string, string>, bool> rewrite,
        Action<string>? unreadableBranch = null)
    {
        if (!IsConditional(type)) return false;

        var changed = false;
        foreach (var branch in parameters.Keys.Where(IsBranchParameter).ToList())
        {
            var json = parameters[branch];
            if (string.IsNullOrWhiteSpace(json)) continue;

            var readable = TryRewriteBranch(
                json,
                child =>
                {
                    var childType = NestsBranches(child) ? ConditionalType : null;
                    var childChanged = RewriteStrings(child, strings => rewrite(null, strings), childLevel: true);
                    foreach (var childParameters in ParameterObjects(child))
                    {
                        childChanged |= RewriteStrings(childParameters, strings => rewrite(childType, strings), childLevel: false);
                    }

                    return childChanged;
                },
                out var rewritten);

            if (!readable)
            {
                unreadableBranch?.Invoke(branch);
                continue;
            }

            if (rewritten is null) continue;

            parameters[branch] = rewritten;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Whether a branch is one that saving, reading and running all read the same way: a JSON array
    /// of objects, each holding only what the action that runs it can take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action reads a child's <c>Type</c> and its <c>Parameters</c>, an object of text values.
    /// So here <c>Parameters</c> (in any casing) must be an object whose values are text or null, a
    /// credential-named property on the child itself must be text or null, and anything else on the
    /// child must be a plain value or a list of plain values. No object repeats a property name,
    /// since the two readers used here do not agree on which value wins.
    /// </para>
    /// <para>
    /// A branch that is anything else is not encrypted, not returned and not run, so a credential
    /// cannot sit somewhere one reader skips and another uses.
    /// </para>
    /// </remarks>
    public static bool IsReadableBranch(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var child in document.RootElement.EnumerateArray())
            {
                if (!IsReadableChild(child)) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a name or a text value holding half of a surrogate pair
            // parses, and throws when it is read.
            return false;
        }
    }

    private static bool IsReadableChild(JsonElement child)
    {
        if (child.ValueKind != JsonValueKind.Object) return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in child.EnumerateObject())
        {
            var name = property.Name;
            if (!names.Add(name)) return false;

            if (IsChildParametersProperty(name))
            {
                if (!IsTextObject(property.Value)) return false;
            }
            else if (IsSensitiveParameterName(name) && !IsResponseFlag(name))
            {
                if (!IsTextOrNull(property.Value)) return false;
            }
            else if (!IsPlainOrListOfPlain(property.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTextObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name) || !IsTextOrNull(property.Value)) return false;
        }

        return true;
    }

    private static bool IsTextOrNull(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;

        // Read, not only inspected: text that cannot be read throws here and makes the branch unreadable.
        _ = element.GetString();
        return true;
    }

    private static bool IsPlain(JsonElement element) =>
        element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False || IsTextOrNull(element);

    private static bool IsPlainOrListOfPlain(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return IsPlain(element);

        foreach (var item in element.EnumerateArray())
        {
            if (!IsPlain(item)) return false;
        }

        return true;
    }

    /// <summary>Applies <paramref name="rewriteChild"/> to each child of a readable branch.</summary>
    /// <param name="rewritten">The branch written out again, or null when no child changed.</param>
    /// <returns>False when the branch is not readable (see <see cref="IsReadableBranch"/>).</returns>
    private static bool TryRewriteBranch(string? json, Func<JsonObject, bool> rewriteChild, out string? rewritten)
    {
        rewritten = null;
        if (!IsReadableBranch(json)) return false;

        try
        {
            if (JsonNode.Parse(json!) is not JsonArray children) return false;

            var changed = false;
            foreach (var child in children)
            {
                if (child is not JsonObject childObject) return false;

                changed |= rewriteChild(childObject);
            }

            if (changed) rewritten = children.ToJsonString(BranchJson);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            rewritten = null;
            return false;
        }
    }

    private static bool NestsBranches(JsonObject child) =>
        child.Any(property =>
            string.Equals(property.Key, ChildTypeProperty, StringComparison.OrdinalIgnoreCase)
            && IsConditional(StringOf(property.Value)));

    private static List<JsonObject> ParameterObjects(JsonObject child) =>
        child.Where(property => IsChildParametersProperty(property.Key))
            .Select(property => property.Value)
            .OfType<JsonObject>()
            .ToList();

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <param name="childLevel">
    /// True for the child object itself, where the response's own flags are not credentials and
    /// are left alone.
    /// </param>
    private static bool RewriteStrings(JsonObject target, Func<Dictionary<string, string>, bool> rewrite, bool childLevel)
    {
        var strings = new Dictionary<string, string>();
        foreach (var property in target)
        {
            if (childLevel && IsResponseFlag(property.Key)) continue;
            if (StringOf(property.Value) is { } text) strings[property.Key] = text;
        }

        var before = strings.Keys.ToList();
        if (!rewrite(strings)) return false;

        foreach (var name in before.Where(removed => !strings.ContainsKey(removed)))
        {
            target.Remove(name);
        }

        foreach (var (name, text) in strings)
        {
            target[name] = JsonValue.Create(text);
        }

        return true;
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

    /// <summary>
    /// The same, for an action as the API returns it: a Conditional's branches also lose their
    /// children's credential values, and each child says whether it has a secret set.
    /// </summary>
    public static Dictionary<string, string> WithoutSecret(string? type, IReadOnlyDictionary<string, string> parameters) =>
        WithoutSecret(type, parameters, out _);

    /// <param name="unreadableBranches">
    /// The branches left out of the copy because they are not readable (see
    /// <see cref="IsReadableBranch"/>). Nothing has checked such a branch for credentials, so it is
    /// named here instead of returned.
    /// </param>
    public static Dictionary<string, string> WithoutSecret(
        string? type, IReadOnlyDictionary<string, string> parameters, out List<string> unreadableBranches)
    {
        unreadableBranches = [];

        var copy = WithoutSecret(parameters);
        if (!IsConditional(type)) return copy;

        foreach (var branch in copy.Keys.Where(IsBranchParameter).ToList())
        {
            var json = copy[branch];
            if (string.IsNullOrWhiteSpace(json)) continue;

            if (!TryRewriteBranch(json, WithoutChildSecrets, out var redacted))
            {
                copy.Remove(branch);
                unreadableBranches.Add(branch);
                continue;
            }

            if (redacted is not null) copy[branch] = redacted;
        }

        return copy;
    }

    private static bool WithoutChildSecrets(JsonObject child)
    {
        var nestsBranches = NestsBranches(child);
        var secretSet = false;
        var unreadable = new JsonArray();

        // On the child itself too, beside Type: the action ignores a key there, and it is still a
        // credential somebody typed. The flags are dropped with them and written again below.
        foreach (var name in child.Select(property => property.Key).Where(key => IsSensitiveParameterName(key) || IsResponseFlag(key)).ToList())
        {
            child.Remove(name);
        }

        foreach (var parameters in ParameterObjects(child))
        {
            secretSet |= parameters.Any(property =>
                property.Key == SecretParameter && !string.IsNullOrWhiteSpace(StringOf(property.Value)));

            foreach (var name in parameters.Select(property => property.Key).Where(IsSensitiveParameterName).ToList())
            {
                parameters.Remove(name);
            }

            if (!nestsBranches) continue;

            foreach (var branch in parameters.Select(property => property.Key).Where(IsBranchParameter).ToList())
            {
                var json = StringOf(parameters[branch]);
                if (string.IsNullOrWhiteSpace(json)) continue;

                if (!TryRewriteBranch(json, WithoutChildSecrets, out var redacted))
                {
                    parameters.Remove(branch);
                    unreadable.Add((JsonNode?)JsonValue.Create(branch));
                    continue;
                }

                if (redacted is not null) parameters[branch] = JsonValue.Create(redacted);
            }
        }

        child[ChildSecretSetProperty] = JsonValue.Create(secretSet);
        if (unreadable.Count > 0) child[ChildUnreadableBranchesProperty] = unreadable;
        return true;
    }
}
