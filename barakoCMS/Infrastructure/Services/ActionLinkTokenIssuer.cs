using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.WebUtilities;

namespace barakoCMS.Infrastructure.Services;

/// <summary>What a signed action link says: one transition, on one entry, for one user.</summary>
internal sealed record ActionLinkClaims(
    [property: JsonPropertyName("j")] Guid Id,
    [property: JsonPropertyName("c")] Guid ContentId,
    [property: JsonPropertyName("t")] string Transition,
    [property: JsonPropertyName("u")] Guid UserId,
    [property: JsonPropertyName("f")] string FromState,
    [property: JsonPropertyName("s")] string Tenant,
    [property: JsonPropertyName("e")] long ExpiresAtUnix)
{
    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);
}

/// <summary>
/// Issues and reads the signed tokens behind <c>{{links.action "Approve"}}</c>.
/// </summary>
/// <remarks>
/// Off unless <c>Workflows:ActionLinks:Enabled</c> is true. Expiry is <c>Workflows:ActionLinks:ExpiryHours</c>,
/// 72 by default.
///
/// The token is the claims and an HMAC of them, keyed from the JWT key under its own label, so a
/// login token can never be read as an action link or the other way round. Single use is not in
/// the token: the first confirmation stores the id as an <c>ActionLinkUse</c>.
///
/// A token is never logged, and nothing here takes a logger.
/// </remarks>
internal sealed class ActionLinkTokenIssuer(IConfiguration configuration, TenantContext tenant)
{
    public const string EnabledKey = "Workflows:ActionLinks:Enabled";
    public const string ExpiryHoursKey = "Workflows:ActionLinks:ExpiryHours";
    public const int DefaultExpiryHours = 72;

    private const string Label = "barakocms.action-link.v1";
    private const int MaxTokenLength = 1024;

    public bool Enabled => configuration.GetValue(EnabledKey, false) && Key() is not null;

    public TimeSpan Lifetime
    {
        get
        {
            var hours = configuration.GetValue(ExpiryHoursKey, DefaultExpiryHours);
            return TimeSpan.FromHours(hours is > 0 and <= 24 * 365 ? hours : DefaultExpiryHours);
        }
    }

    /// <summary>The token for one transition on one entry for one user, or null when action links are off.</summary>
    public string? Issue(Guid contentId, string transition, Guid userId, string fromState, DateTimeOffset now)
    {
        if (!Enabled || Key() is not { } key)
            return null;

        var claims = new ActionLinkClaims(
            Guid.NewGuid(), contentId, transition, userId, fromState, tenant.Slug, (now + Lifetime).ToUnixTimeSeconds());

        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return payload + "." + WebEncoders.Base64UrlEncode(Sign(key, payload));
    }

    /// <summary>
    /// The claims of a token that is signed by this deployment, is for this tenant and has not
    /// expired. Null otherwise, and for every reason alike. Whether the link was already used is
    /// not known here.
    /// </summary>
    public ActionLinkClaims? Read(string? token, DateTimeOffset now)
    {
        if (!Enabled || Key() is not { } key || string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
            return null;

        var parts = token.Split('.');
        if (parts.Length != 2)
            return null;

        try
        {
            var given = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(given, Sign(key, parts[0])))
                return null;

            var claims = JsonSerializer.Deserialize<ActionLinkClaims>(WebEncoders.Base64UrlDecode(parts[0]));
            if (claims is null
                || claims.Transition is null
                || claims.FromState is null
                || claims.Tenant != tenant.Slug
                || claims.ExpiresAt <= now)
            {
                return null;
            }

            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Sign(byte[] key, string payload) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));

    private byte[]? Key()
    {
        var jwt = configuration["JWT:Key"];
        if (string.IsNullOrWhiteSpace(jwt))
            jwt = Environment.GetEnvironmentVariable("JWT__Key");

        return string.IsNullOrWhiteSpace(jwt)
            ? null
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt), Encoding.UTF8.GetBytes(Label));
    }
}
