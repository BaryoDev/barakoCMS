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
}
