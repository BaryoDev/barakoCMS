namespace barakoCMS.Features.Content;

/// <summary>
/// Formats and parses the Content document's Marten optimistic-concurrency version (its <c>mt_version</c>
/// column) as an HTTP ETag, so <c>GET</c> and <c>PUT</c> round-trip the same value.
/// </summary>
/// <remarks>
/// #565 / DECISIONS.md D16. A strong ETag: this never claims byte equivalence, only that the version
/// is the one last read, and picking weak-tag syntax would have bought nothing while costing every
/// caller a second parsing branch. One format, quoted, no <c>W/</c> prefix, used on both ends.
/// </remarks>
internal static class ContentETag
{
    public static string Format(Guid version) => $"\"{version:D}\"";

    /// <summary>Parses an ETag or If-Match value back into the version Marten compares against.</summary>
    /// <remarks>
    /// Strips one layer of surrounding quotes and a leading weak-tag marker, so a value copied
    /// verbatim from a <see cref="Format"/> result, or produced by a client library that adds
    /// <c>W/</c> on everything, both parse the same way.
    /// </remarks>
    public static bool TryParse(string? headerValue, out Guid version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        var value = headerValue.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return Guid.TryParse(value, out version);
    }
}
