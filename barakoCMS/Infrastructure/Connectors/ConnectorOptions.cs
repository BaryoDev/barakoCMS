namespace barakoCMS.Infrastructure.Connectors;

/// <summary>
/// What this deployment has configured for connectors, checked once at startup.
/// </summary>
/// <remarks>
/// Validated before the host is built, the same shape as <c>ErasureOptions</c>, because the failure
/// being guarded against is an operator believing credentials are protected in a way they are not,
/// and startup is the only moment that belief is cheap to correct.
/// </remarks>
public sealed class ConnectorOptions
{
    public string? Key { get; init; }

    public bool HasKey => !string.IsNullOrEmpty(Key);

    public static ConnectorOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Key = configuration["Connectors:Key"],
    };

    /// <summary>
    /// Throws when a configured key cannot do what an operator would assume it does.
    /// </summary>
    /// <remarks>
    /// An absent key is not an error here. It means the feature is off, and the endpoints refuse
    /// with the setting named rather than the deployment failing to start over a feature nobody
    /// asked for. What is refused is a key that is present and wrong, because that is the case where
    /// somebody has decided to protect credentials and would otherwise be told nothing.
    /// </remarks>
    public void Validate(IConfiguration configuration)
    {
        if (!HasKey) return;

        if (Key!.Length < ConnectorSecretProtector.MinimumKeyLength)
        {
            throw new InvalidOperationException(
                $"Connectors:Key is {Key.Length} characters. It needs at least "
                + $"{ConnectorSecretProtector.MinimumKeyLength}, the same floor the JWT signing key has, "
                + "because it is the only thing standing between a database dump and every credential "
                + "this instance holds.");
        }

        // The lesson SECURITY.md already records about Mfa:Key falling back to JWT:Key, enforced
        // instead of noted. Sharing key material means one rotation retires two unrelated controls,
        // and the operator finds out from whichever one breaks first.
        foreach (var other in new[] { "JWT:Key", "Mfa:Key", "Secrets:Key" })
        {
            if (string.Equals(configuration[other], Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Connectors:Key is the same value as {other}. Give it its own, so rotating one "
                    + "does not silently retire the other: rotating an encryption key makes everything "
                    + "encrypted under it unreadable, and that has to be one decision at a time.");
            }
        }
    }
}
