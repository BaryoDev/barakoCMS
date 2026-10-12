using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Secrets.Gcp;

/// <summary>
/// Adds Google Secret Manager as a configuration source, so values such as <c>JWT:Key</c> come
/// from the store instead of the environment.
/// </summary>
public static class GcpSecretsConfigurationExtensions
{
    /// <summary>The section that names the secrets to load and the project they live in.</summary>
    public const string Section = "Secrets:Gcp";

    /// <summary>
    /// Loads the latest version of every secret named under <c>Secrets:Gcp:Map</c> onto the
    /// configuration key it is mapped to, for example <c>Secrets:Gcp:Map:JWT:Key = barakocms-jwt-key</c>
    /// (env <c>Secrets__Gcp__Map__JWT__Key</c>) with <c>Secrets:Gcp:Project</c> naming the project. A
    /// value that is a full name, <c>projects/p/secrets/s</c> or <c>projects/p/secrets/s/versions/3</c>,
    /// is used as it is. Call it on <c>builder.Configuration</c> before <c>AddBarakoCMS</c>.
    /// </summary>
    /// <remarks>
    /// Authenticates with Google's application default credentials, which on Google Cloud is the
    /// service account the instance runs as; no key is read from barakoCMS configuration. With no
    /// map this adds nothing and calls nothing, so a host that installs the package and does not
    /// configure it behaves as before. A mapped secret that cannot be read throws, which stops
    /// startup, and the message names the secret and the key it was for, never a value.
    /// </remarks>
    public static IConfigurationBuilder AddBarakoGcpSecrets(this IConfigurationBuilder builder)
    {
        SecretManagerServiceClient? client = null;
        return builder.AddBarakoGcpSecrets((name, ct) => ReadAsync(client ??= SecretManagerServiceClient.Create(), name, ct));
    }

    internal static IConfigurationBuilder AddBarakoGcpSecrets(
        this IConfigurationBuilder builder,
        Func<string, CancellationToken, Task<string?>> read)
    {
        var current = CurrentConfiguration(builder);
        var map = SecretMap.Read(current.GetSection($"{Section}:Map"));
        if (map.Count == 0)
        {
            return builder;
        }

        var project = current[$"{Section}:Project"];
        var names = map.ToDictionary(
            pair => pair.Key,
            pair => VersionName(project, pair.Key, pair.Value),
            StringComparer.OrdinalIgnoreCase);

        return builder.Add(new MappedSecretsSource(Section, names, read));
    }

    internal static string VersionName(string? project, string key, string secret)
    {
        if (secret.StartsWith("projects/", StringComparison.Ordinal))
        {
            return secret.Contains("/versions/", StringComparison.Ordinal) ? secret : $"{secret}/versions/latest";
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                $"{Section}: the secret '{secret}', mapped to {key}, names no project and {Section}:Project is not set. "
                + "Set the project, or map the full name projects/<project>/secrets/<secret>.");
        }

        return $"projects/{project.Trim()}/secrets/{secret}/versions/latest";
    }

    private static IConfiguration CurrentConfiguration(IConfigurationBuilder builder) =>
        builder as IConfiguration ?? builder.Build();

    private static async Task<string?> ReadAsync(SecretManagerServiceClient client, string name, CancellationToken ct)
    {
        try
        {
            var response = await client.AccessSecretVersionAsync(name, ct);
            return response.Payload.Data.ToStringUtf8();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (RpcException ex)
        {
            throw new SecretUnreadableException($"Google answered {ex.StatusCode}");
        }
    }
}
