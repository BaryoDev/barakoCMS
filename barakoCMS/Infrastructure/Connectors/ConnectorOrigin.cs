namespace barakoCMS.Infrastructure.Connectors;

/// <summary>
/// The scheme, host and port a connector's credentials were entered for.
/// </summary>
/// <remarks>
/// Compared normalised: the host lowercased and in its punycode form, and the port made explicit, so
/// <c>https://API.example:443</c> and <c>https://api.example</c> are one origin. Userinfo and path
/// are not part of it. A URL that does not parse has no origin and matches nothing.
/// </remarks>
internal static class ConnectorOrigin
{
    internal static (string Scheme, string Host, int Port)? Of(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        return Of(uri);
    }

    internal static (string Scheme, string Host, int Port) Of(Uri uri) =>
        (uri.Scheme.ToLowerInvariant(), uri.IdnHost.ToLowerInvariant(), uri.Port);

    internal static bool Same(string? a, string? b) => Of(a) is { } left && Of(b) is { } right && left == right;

    /// <summary>
    /// Whether the base URL moved: a different scheme, host or port (case ignored), a different path
    /// (case counts, a trailing slash does not) or a different query. A URL that does not parse
    /// counts as moved unless it is the same text, so a grant is dropped when in doubt.
    /// </summary>
    internal static bool Moved(string? before, string? after)
    {
        if (string.Equals(before?.Trim(), after?.Trim(), StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(before?.Trim(), UriKind.Absolute, out var left)
            || !Uri.TryCreate(after?.Trim(), UriKind.Absolute, out var right))
        {
            return true;
        }

        return Of(left) != Of(right)
            || !string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
            || !string.Equals(left.Query, right.Query, StringComparison.Ordinal);
    }
}
