namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Refuses a weak <c>InitialAdmin:Password</c> outside Development. The first admin is a
/// SuperAdmin, so a password published in a sample or a doc is a public key to the instance.
/// </summary>
/// <remarks>
/// Unset is not checked here: the seeder generates a strong password in that case. The message
/// names the setting and the rule that failed and never repeats the value.
/// </remarks>
internal static class InitialAdminGuard
{
    public const int MinLength = 12;

    /// <summary>Values the compose files, manifests and docs of this repository have shown as examples or defaults.</summary>
    private static readonly string[] KnownExamples =
    [
        "changeme-in-production",
        "change-me-to-a-strong-password",
        "local-only-password",
        "your_admin_password",
        "admin",
        "password",
        "password123!",
        "admin123",
    ];

    /// <summary>Every placeholder in the shipped manifests starts with this.</summary>
    private const string PlaceholderPrefix = "REPLACE_THIS";

    public static void Validate(string? username, string? password, bool isDevelopment)
    {
        if (isDevelopment || string.IsNullOrEmpty(password))
        {
            return;
        }

        // Whitespace around a value is not part of a password anyone means to set, so the checks
        // read the trimmed value and a value that is only whitespace is refused.
        var reason = Reason(username, password.Trim());
        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"InitialAdmin:Password is refused: {reason}. Set InitialAdmin:Password (environment "
              + "variable InitialAdmin__Password) to a random value of at least "
              + $"{MinLength} characters, or leave it unset and the first boot generates one.");
        }
    }

    private static string? Reason(string? username, string password)
    {
        if (password.Length < MinLength)
        {
            return $"it is shorter than {MinLength} characters";
        }

        if (!string.IsNullOrEmpty(username)
            && (Same(password, username) || Same(password, $"{username}@example.com")))
        {
            return "it is the same as the user name or its email";
        }

        if (password.StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase)
            || KnownExamples.Any(example => Same(password, example)))
        {
            return "it is an example or default value from the samples and docs";
        }

        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
