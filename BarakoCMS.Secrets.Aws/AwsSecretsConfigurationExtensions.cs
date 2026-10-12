using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Secrets.Aws;

/// <summary>
/// Adds AWS Secrets Manager as a configuration source, so values such as <c>JWT:Key</c> come from
/// the store instead of the environment.
/// </summary>
public static class AwsSecretsConfigurationExtensions
{
    /// <summary>The section that names the secrets to load and the region to load them from.</summary>
    public const string Section = "Secrets:Aws";

    /// <summary>
    /// Loads every secret named under <c>Secrets:Aws:Map</c> onto the configuration key it is mapped
    /// to, for example <c>Secrets:Aws:Map:JWT:Key = barakocms-jwt-key</c> (env
    /// <c>Secrets__Aws__Map__JWT__Key</c>). Call it on <c>builder.Configuration</c> before
    /// <c>AddBarakoCMS</c>.
    /// </summary>
    /// <remarks>
    /// Authenticates with the AWS SDK's default credentials, which on AWS is the instance or task
    /// role; no key is read from barakoCMS configuration. <c>Secrets:Aws:Region</c> sets the region,
    /// otherwise the SDK's own lookup decides. With no map this adds nothing and calls nothing, so a
    /// host that installs the package and does not configure it behaves as before. A mapped secret
    /// that cannot be read throws, which stops startup, and the message names the secret and the key
    /// it was for, never a value.
    /// </remarks>
    public static IConfigurationBuilder AddBarakoAwsSecrets(this IConfigurationBuilder builder)
    {
        var region = CurrentConfiguration(builder)[$"{Section}:Region"];
        AmazonSecretsManagerClient? client = null;
        return builder.AddBarakoAwsSecrets((secretId, ct) => ReadAsync(client ??= CreateClient(region), secretId, ct));
    }

    internal static IConfigurationBuilder AddBarakoAwsSecrets(
        this IConfigurationBuilder builder,
        Func<string, CancellationToken, Task<string?>> read)
    {
        var map = SecretMap.Read(CurrentConfiguration(builder).GetSection($"{Section}:Map"));
        if (map.Count == 0)
        {
            return builder;
        }

        return builder.Add(new MappedSecretsSource(Section, map, read));
    }

    private static IConfiguration CurrentConfiguration(IConfigurationBuilder builder) =>
        builder as IConfiguration ?? builder.Build();

    private static AmazonSecretsManagerClient CreateClient(string? region) =>
        string.IsNullOrWhiteSpace(region)
            ? new AmazonSecretsManagerClient()
            : new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));

    private static async Task<string?> ReadAsync(IAmazonSecretsManager client, string secretId, CancellationToken ct)
    {
        try
        {
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, ct);
            return response.SecretString;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
        catch (AmazonSecretsManagerException ex)
        {
            throw new SecretUnreadableException($"AWS answered {ex.ErrorCode}");
        }
    }
}
