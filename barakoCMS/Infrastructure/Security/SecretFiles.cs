using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Reads secrets from files (Docker secrets, Kubernetes secret volumes) into configuration, so an
/// operator does not have to put them in environment variables.
/// </summary>
/// <remarks>
/// One file per key, named with <c>__</c> as the section separator: <c>/run/secrets/JWT__Key</c> is
/// <c>JWT:Key</c>. The values are added after every other source, so a secret file wins over an
/// environment variable, a command-line argument and an appsettings value for the same key. The startup guards read
/// configuration, so they apply to a file value the same as to any other.
///
/// The framework's key-per-file provider removes one trailing newline. <c>echo key &gt; file</c>
/// leaves one, but an editor or a template can leave more, and a key with a hidden newline is a
/// different secret than the one typed, so every trailing carriage return and line feed is removed
/// here. A file's UTF-8 byte order mark is dropped, and an empty file sets an empty value. Nothing else is
/// trimmed.
/// </remarks>
public static class SecretFiles
{
    internal const string DirectorySetting = "BARAKO_SECRETS_DIR";

    internal const string DefaultDirectory = "/run/secrets";

    /// <summary>
    /// Adds the secret files named by <c>BARAKO_SECRETS_DIR</c> (default <c>/run/secrets</c>). A blank
    /// value means the default. Call it right after <c>WebApplication.CreateBuilder</c> and before
    /// anything reads configuration, so a secret file wins over every other source and the startup
    /// guards see it. A file that cannot be read, or a directory that cannot be listed, throws an
    /// <see cref="InvalidOperationException"/> naming it, never a content. A custom host built on the
    /// package calls this to opt in.
    /// </summary>
    public static IConfigurationBuilder AddSecretFilesFromEnvironment(
        this IConfigurationBuilder builder,
        Func<string, string?>? environment = null,
        string defaultDirectory = "/run/secrets")
    {
        var configured = (environment ?? Environment.GetEnvironmentVariable)(DirectorySetting);
        var directory = string.IsNullOrWhiteSpace(configured) ? defaultDirectory : configured.Trim();
        return builder.AddSecretFiles(directory);
    }

    /// <summary>
    /// Adds every file in <paramref name="directory"/> as a configuration key. A directory that does
    /// not exist adds nothing. A file that cannot be read, or a directory that cannot be listed, throws and names the file or
    /// the directory, never a content.
    /// </summary>
    internal static IConfigurationBuilder AddSecretFiles(this IConfigurationBuilder builder, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return builder;
        }

        // The provider's own failure names a path. This names the file and says what to check, and
        // runs first so the message is ours whichever call inside the provider would have failed.
        string[] listed;
        try
        {
            listed = Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The secrets directory {directory} cannot be listed. Check that the user the API runs as "
              + "(uid 1654 in the shipped image) can read and enter it.");
        }

        foreach (var file in listed)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || name.StartsWith("ignore.", StringComparison.OrdinalIgnoreCase))
            {
                continue; // the provider skips these too (k8s keeps its symlink bookkeeping in ..data)
            }

            try
            {
                using var stream = File.OpenRead(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The secret file '{name}' in {directory} cannot be read. Check that the user the API runs as "
                  + "(uid 1654 in the shipped image) owns it or can read it.");
            }
        }

        var files = new ConfigurationBuilder()
            .AddKeyPerFile(options =>
            {
                options.FileProvider = new PhysicalFileProvider(directory);
                options.Optional = true;
            })
            .Build();

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in files.AsEnumerable())
        {
            // A parent such as "JWT" comes back with no value of its own.
            if (pair.Value is not null)
            {
                values[pair.Key] = pair.Value.TrimEnd('\r', '\n');
            }
        }

        return builder.AddInMemoryCollection(values);
    }
}
