using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Secrets.Aws;

/// <summary>
/// The configuration keys to fill and the secret each one comes from. Under the map section a leaf
/// at <c>JWT:Key</c> holding <c>barakocms-jwt-key</c> means "JWT:Key is the secret
/// barakocms-jwt-key", so the target key keeps its own colons and an environment variable such as
/// <c>Secrets__Aws__Map__JWT__Key</c> reads naturally.
/// </summary>
internal static class SecretMap
{
    /// <summary>The map in key order, so a failing start always names the same secret first.</summary>
    public static IReadOnlyDictionary<string, string> Read(IConfigurationSection map) =>
        new SortedDictionary<string, string>(
            map.AsEnumerable(makePathsRelative: true)
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!.Trim(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>A secret the store refused or could not serve, for a reason other than its absence.</summary>
internal sealed class SecretUnreadableException(string reason) : Exception(reason);

internal sealed class MappedSecretsSource(
    string section,
    IReadOnlyDictionary<string, string> map,
    Func<string, CancellationToken, Task<string?>> read) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new MappedSecretsProvider(section, map, read);
}

internal sealed class MappedSecretsProvider(
    string section,
    IReadOnlyDictionary<string, string> map,
    Func<string, CancellationToken, Task<string?>> read) : ConfigurationProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads every mapped secret, or throws naming the first that cannot be read. Configuration
    /// loading is synchronous, so this blocks; it runs once, at startup, before anything serves.
    /// </summary>
    public override void Load()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var cts = new CancellationTokenSource(Timeout);
        foreach (var (key, secret) in map)
        {
            string? value;
            try
            {
                value = read(secret, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                throw Refused(secret, key, $"was not read within {Timeout.TotalSeconds:0} seconds", ex);
            }
            catch (Exception ex)
            {
                // No reader puts a secret value into an exception: it fails before the store answers, or
                // with a message of its own that names the problem, never the payload.
                throw Refused(secret, key, $"could not be read: {ex.Message}", ex);
            }

            if (value is null)
            {
                throw Refused(secret, key, "does not exist");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Refused(secret, key, "is empty");
            }

            values[key] = value;
        }

        Data = values;
    }

    private InvalidOperationException Refused(string secret, string key, string what, Exception? inner = null) =>
        new($"{section}: the secret '{secret}', mapped to {key}, {what}. Startup stops here rather than "
            + $"run without {key}. Check the name under {section}:Map and that this instance's identity may read it.",
            inner);
}
