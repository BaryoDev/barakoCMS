namespace BarakoCMS.Tests;

/// <summary>
/// Throwaway names for tests that need a value no other test run will have used.
/// </summary>
/// <remarks>
/// Lengths and casing matter: content type, tenant and role names have limits and are lower-cased
/// on some paths. Each helper keeps one spelling that several classes used verbatim.
/// </remarks>
internal static class TestNames
{
    /// <summary>The prefix followed by ten characters of a GUID, with no separator.</summary>
    public static string Compact(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    /// <summary>The prefix, a dash and a GUID, cut to 20 characters and lower-cased.</summary>
    public static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();
}
