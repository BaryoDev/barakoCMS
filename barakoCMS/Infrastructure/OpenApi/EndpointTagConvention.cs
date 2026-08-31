namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>
/// Derives an endpoint's OpenAPI tag from the namespace it already lives in.
/// </summary>
/// <remarks>
/// Generators group methods by tag, so a document where every operation carries one tag generates
/// one class with every method on it. Tagging by convention means a new endpoint is grouped
/// correctly by existing where it belongs, rather than by remembering a <c>Tags(...)</c> line that
/// silently falls back to the catch-all when it is forgotten.
///
/// <code>
/// barakoCMS.Features.Content.Create      -> Content
/// barakoCMS.Features.ContentType.List    -> ContentType
/// BarakoCMS.Accounting.Features.Reports  -> Accounting
/// BarakoCMS.Analytics.Umami.Features     -> Analytics.Umami
/// BarakoCMS.ExternalAuth                 -> ExternalAuth
/// </code>
///
/// The first namespace segment is the assembly root and is always dropped: it is the same for
/// every core endpoint and carries no grouping information. What identifies a module is what sits
/// between that root and <c>Features</c>; what identifies a core slice is what sits after it.
/// A module whose endpoints are not under a <c>Features</c> namespace (Email.Resend,
/// ExternalAuth, FeatureFlags, Portability) falls back to everything after the root, which is the
/// module name.
///
/// An endpoint that sets its own tag keeps it. See <c>NamespaceTagProcessor</c>, which only fills
/// in a tag where there is none.
/// </remarks>
public static class EndpointTagConvention
{
    private const string FeaturesSegment = "Features";

    /// <summary>The tag for a type in <paramref name="ns"/>, or null when no tag can be derived.</summary>
    public static string? ForNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return null;

        var parts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var featuresAt = Array.FindIndex(
            parts, p => string.Equals(p, FeaturesSegment, StringComparison.OrdinalIgnoreCase));

        if (featuresAt < 0)
        {
            // No Features segment: the module name is everything after the assembly root.
            return parts.Length > 1 ? string.Join('.', parts[1..]) : parts[0];
        }

        // Something between the root and Features means a module, and that something names it.
        if (featuresAt > 1)
            return string.Join('.', parts[1..featuresAt]);

        // Core: the slice name is the segment after Features.
        return featuresAt + 1 < parts.Length ? parts[featuresAt + 1] : null;
    }

    /// <summary>The tag for an endpoint type, or null when no tag can be derived.</summary>
    public static string? ForType(Type? endpointType) => ForNamespace(endpointType?.Namespace);
}
