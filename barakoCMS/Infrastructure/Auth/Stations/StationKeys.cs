using System.Security.Cryptography;
using System.Text;

namespace barakoCMS.Infrastructure.Auth.Stations;

/// <summary>
/// Station key format: <c>bsta_</c> plus 32 random bytes in URL-safe base64, 256 bits. Same shape
/// and hashing as API keys, with a different prefix so the auth selector can tell them apart.
/// </summary>
public static class StationKeys
{
    public const string Prefix = "bsta_";

    public static (string Secret, string DisplayPrefix, string Hash) Generate()
    {
        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var secret = Prefix + random;
        return (secret, Prefix + random[..8], Hash(secret));
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// The station key on this request, or null. Read from <c>Authorization: Bearer</c>, and on hub
    /// paths also from <c>access_token</c> in the query string, which is where SignalR puts it when
    /// the transport cannot carry a header.
    /// </summary>
    public static string? Presented(HttpRequest request)
    {
        const string bearer = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith(bearer + Prefix, StringComparison.Ordinal))
            return header[bearer.Length..].Trim();

        // The query string is a fallback for a transport that cannot send a header. A request that
        // already carries some other Authorization header keeps that credential.
        if (header.Length == 0
            && request.Path.StartsWithSegments("/hubs")
            && request.Query["access_token"].ToString() is { } query
            && query.StartsWith(Prefix, StringComparison.Ordinal))
            return query;

        return null;
    }
}
