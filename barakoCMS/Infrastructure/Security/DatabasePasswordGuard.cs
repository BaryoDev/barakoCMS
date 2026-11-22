namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Refuses a database password the repository shows as an example, outside Development. The compose
/// files, the env examples and the docs all name one, so a deployment that kept it is running on a
/// password anyone can read.
/// </summary>
/// <remarks>
/// Reads the connection string the app resolves (<c>ConnectionStrings:DefaultConnection</c>, or
/// <c>DATABASE_URL</c>, which wins when both are set). An unset or unreadable string is not checked
/// here: the real resolve reports it with its own message. The message names the setting and the
/// rule and never repeats the value.
/// </remarks>
internal static class DatabasePasswordGuard
{
    /// <summary>Passwords the compose files, env examples and docs of this repository have shown as examples or defaults.</summary>
    private static readonly string[] KnownExamples =
    [
        "postgres",
        "change-me-to-a-strong-password",
        "changeme-in-production",
        "local-only-password",
        "password",
    ];

    /// <summary>Every placeholder in the shipped manifests starts with this.</summary>
    private const string PlaceholderPrefix = "REPLACE_THIS";

    public static void Validate(string? connectionString, bool isDevelopment, Func<string, string?>? environment = null)
    {
        if (isDevelopment || string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // Npgsql falls back to PGPASSWORD when the string has no password, so that value counts too.
        var password = PasswordOf(connectionString)
            ?? NullIfBlank((environment ?? Environment.GetEnvironmentVariable)("PGPASSWORD"));
        if (password is null)
        {
            return;
        }

        // Whitespace around a value is not part of a password anyone means to set.
        var trimmed = password.Trim();
        if (trimmed.StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase)
            || KnownExamples.Any(example => string.Equals(trimmed, example, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The database password in ConnectionStrings:DefaultConnection (or DATABASE_URL) is refused: "
              + "it is an example or default value from the samples and docs. Set a random password "
              + "(environment variable DB_PASSWORD in the compose files) before starting outside Development.");
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? PasswordOf(string connectionString)
    {
        try
        {
            var password = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Password;
            return string.IsNullOrEmpty(password) ? null : password;
        }
        catch (ArgumentException)
        {
            // Not a connection string Npgsql reads. The store reports that with its own message.
            return null;
        }
    }
}
