using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// The keys one class of stored secret is encrypted under: several, each with an id, one of them
/// current.
/// </summary>
/// <remarks>
/// <para>
/// Configured per class, under <c>{Section}:Keys:{id}</c> with <c>{Section}:CurrentKey</c> naming
/// the one new writes use. A value written under a ring carries its key id,
/// <c>enc:v1:{id}:{envelope}</c>, so it decrypts under the key that wrote it whatever is current.
/// Base64 has no colon, so an id cannot be mistaken for the start of an envelope.
/// </para>
/// <para>
/// A value with no key id was written before the ring, under the single key the class used then.
/// That key is tried first, then every key in the ring. It only reads: once a ring is configured,
/// nothing new is written under it, which is how the JWT signing key stops being the key stored
/// secrets depend on. Nothing has to be migrated up front; <see cref="SecretRewrapService"/> moves
/// old values to the current key at startup.
/// </para>
/// <para>
/// With no ring configured nothing changes: the single key reads and writes, and writes carry no id,
/// so a deployment that never opts in stores exactly what it stored before.
/// </para>
/// </remarks>
internal sealed partial class KeyRing
{
    /// <summary>Shortest key material a ring entry may hold, the floor JWT:Key and Connectors:Key have.</summary>
    internal const int MinimumKeyLength = 32;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly IReadOnlyList<byte[]> _unkeyedCandidates;
    private readonly byte[]? _currentKey;
    private readonly bool _prefixUnkeyedWrites;

    private KeyRing(
        Dictionary<string, byte[]> keys, string? currentId, byte[]? currentKey,
        IReadOnlyList<byte[]> unkeyedCandidates, bool prefixUnkeyedWrites)
    {
        _keys = keys;
        CurrentId = currentId;
        _currentKey = currentKey;
        _unkeyedCandidates = unkeyedCandidates;
        _prefixUnkeyedWrites = prefixUnkeyedWrites;
    }

    /// <summary>The id new writes carry, or null when no ring is configured.</summary>
    public string? CurrentId { get; }

    /// <summary>Whether there is a key to write with at all.</summary>
    public bool CanProtect => _currentKey is not null;

    /// <summary>Reads a class's ring from configuration, refusing one that is ambiguous or weak.</summary>
    /// <param name="section">The configuration section the class keeps its key in: Secrets, Mfa or Connectors.</param>
    /// <param name="legacyMaterial">
    /// The single key the class used before rings, which reads values with no key id. When there is
    /// no ring it is also the key new values are written under.
    /// </param>
    /// <param name="prefixUnkeyedWrites">
    /// Whether a value written with no ring carries <see cref="AesGcmEnvelope.VersionPrefix"/>, which
    /// is what each class already stored: Secrets does, Mfa and Connectors do not.
    /// </param>
    public static KeyRing FromConfiguration(
        IConfiguration config, string section, string? legacyMaterial, bool prefixUnkeyedWrites)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var jwtKey = config["JWT:Key"];

        foreach (var entry in config.GetSection($"{section}:Keys").GetChildren())
        {
            if (!KeyIdPattern().IsMatch(entry.Key))
            {
                throw new InvalidOperationException(
                    $"{section}:Keys has an entry named '{entry.Key}'. A key id is 1 to 32 letters, digits, "
                    + "'-' or '_', because it is written into every value the key encrypts.");
            }

            // Skipping an empty entry would leave the legacy key writing new values with no id, and
            // nobody would be told: a vault reference that resolved to nothing must stop startup.
            if (string.IsNullOrEmpty(entry.Value))
            {
                throw new InvalidOperationException(
                    $"{section}:Keys:{entry.Key} is empty. Give it key material, or remove the entry.");
            }

            if (entry.Value.Length < MinimumKeyLength)
            {
                throw new InvalidOperationException(
                    $"{section}:Keys:{entry.Key} is {entry.Value.Length} characters. It needs at least {MinimumKeyLength}.");
            }

            if (string.Equals(entry.Value, jwtKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{section}:Keys:{entry.Key} is the same value as JWT:Key. Rotating the signing key would "
                    + "then retire every stored secret under it. Give the ring its own key: a value written "
                    + "under the JWT key before the ring still reads, and is re-wrapped to the current key.");
            }

            keys[entry.Key] = AesGcmEnvelope.DeriveKey(entry.Value);
        }

        var legacy = string.IsNullOrEmpty(legacyMaterial) ? null : AesGcmEnvelope.DeriveKey(legacyMaterial);
        var unkeyed = new List<byte[]>();
        if (legacy is not null) unkeyed.Add(legacy);
        unkeyed.AddRange(keys.Values);

        var currentId = config[$"{section}:CurrentKey"];

        if (keys.Count == 0)
        {
            if (!string.IsNullOrEmpty(currentId))
            {
                throw new InvalidOperationException(
                    $"{section}:CurrentKey is '{currentId}', but there are no keys under {section}:Keys.");
            }

            return new KeyRing(keys, currentId: null, legacy, unkeyed, prefixUnkeyedWrites);
        }

        if (string.IsNullOrEmpty(currentId))
        {
            if (keys.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{section}:Keys has {keys.Count} keys and {section}:CurrentKey does not say which one "
                    + "new values are written under.");
            }

            currentId = keys.Keys.Single();
        }

        if (!keys.TryGetValue(currentId, out var currentKey))
        {
            throw new InvalidOperationException(
                $"{section}:CurrentKey is '{currentId}', which is not one of the ids under {section}:Keys.");
        }

        return new KeyRing(keys, currentId, currentKey, unkeyed, prefixUnkeyedWrites);
    }

    public string Protect(string plaintext)
    {
        if (_currentKey is null) throw new InvalidOperationException("No key is configured to encrypt with.");

        var envelope = AesGcmEnvelope.Protect(_currentKey, plaintext);
        if (CurrentId is not null) return $"{AesGcmEnvelope.VersionPrefix}{CurrentId}:{envelope}";

        return _prefixUnkeyedWrites ? AesGcmEnvelope.VersionPrefix + envelope : envelope;
    }

    /// <summary>Decrypts under whichever key wrote the value, or returns null when no key in the ring opens it.</summary>
    public string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;

        var (keyId, envelope) = AesGcmEnvelope.Split(protectedValue);

        if (keyId is not null)
        {
            return _keys.TryGetValue(keyId, out var key) ? TryOpen(key, envelope) : null;
        }

        foreach (var candidate in _unkeyedCandidates)
        {
            if (TryOpen(candidate, envelope) is { } plaintext) return plaintext;
        }

        return null;
    }

    /// <summary>
    /// Whether a value is already written the way a new write would be, so re-wrapping it changes
    /// nothing. Says nothing about whether it opens.
    /// </summary>
    public bool IsCurrent(string protectedValue)
    {
        var (keyId, _) = AesGcmEnvelope.Split(protectedValue);
        return string.Equals(keyId, CurrentId, StringComparison.Ordinal);
    }

    private static string? TryOpen(byte[] key, string envelope)
    {
        try
        {
            return AesGcmEnvelope.Unprotect(key, envelope);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex KeyIdPattern();
}

/// <summary>A protector whose values live under a <see cref="KeyRing"/>, so they can be re-wrapped.</summary>
internal interface IKeyRingProtector
{
    KeyRing Ring { get; }
}
