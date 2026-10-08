namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Decides whether a name (a setting key, a workflow action parameter) reads as holding a
/// credential. Every surface that asks the question asks it here, so two surfaces cannot disagree
/// about the same word.
/// </summary>
/// <remarks>
/// <para>
/// Names are free-form on both surfaces. A workflow action's parameters carry whatever the third
/// party calls its credential (a Password, a Token, an ApiKey), and a setting key is typed by an
/// operator. So the rule works on the name alone.
/// </para>
/// <para>
/// Matching is on a substring, without regard to case, and deliberately errs towards yes. A
/// parameter called <c>TokenUrl</c> is not a credential and is still treated as one, which costs an
/// operator one lookup or one renamed key. The other way round costs a credential.
/// </para>
/// <para>
/// Adding a word changes what each caller does with names that already exist: a workflow parameter
/// under that name stops being returned and is encrypted by the startup pass, and a setting under
/// it can no longer be saved, only cleared. Removing a word leaves a value that was protected in
/// clear.
/// </para>
/// <para>
/// The console cannot call this, so <c>GET /api/meta/describe</c> publishes <see cref="Words"/> as
/// <c>credentialNameParts</c> and the console masks its inputs by those. A word added here reaches
/// it with no change on that side.
/// </para>
/// </remarks>
internal static class CredentialNames
{
    private static readonly string[] Parts =
    [
        "secret", "password", "passwd", "pwd", "token", "apikey", "api_key",
        "credential", "privatekey", "private_key", "accesskey", "access_key",

        // What an HTTP header carrying a credential is called, and the scheme it is sent under.
        // Not "auth" alone, which would also match "author".
        "authorization", "bearer",
    ];

    public static bool IsCredential(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var part in Parts)
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The words a name is matched against, in the order they are checked.</summary>
    public static IReadOnlyList<string> Words { get; } = Array.AsReadOnly(Parts);
}
