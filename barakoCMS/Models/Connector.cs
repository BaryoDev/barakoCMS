namespace barakoCMS.Models;

/// <summary>
/// A third party this instance can call, held as configuration rather than as code.
/// </summary>
/// <remarks>
/// Jira needs a token and a base URL, Twilio an account SID and an auth token and a from-number, a
/// plain REST API a header. Each of those is otherwise a module with its own config keys and its own
/// code, which is the thing #325 exists to stop.
///
/// **No secret is on this document.** They live in <see cref="ConnectorSecret"/>, encrypted, and the
/// read path never joins them. That separation is the whole point: a bug that returns a Connector
/// over the API cannot leak a token, because the token is not in the object to leak. Only the NAMES
/// of the secrets held are here, so an admin screen can say which are set without ever handling one.
/// </remarks>
public class Connector
{
    public Guid Id { get; set; }

    /// <summary>The admin's label, "Company Jira".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What a request definition references, "company-jira". Unique per tenant.</summary>
    public string Slug { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public ConnectorAuth Auth { get; set; } = ConnectorAuth.None;

    /// <summary>Non-secret configuration: header names, scopes, a from-number.</summary>
    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>
    /// The names of the secrets this connector holds, never the values.
    /// </summary>
    /// <remarks>
    /// Kept here so the list endpoint can say "an ApiToken is set" without reading, decrypting or
    /// joining anything. Asking the secret store instead would put a decryptable value one mistake
    /// away from a response body.
    /// </remarks>
    public List<string> SecretKeys { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The path the test button requests, relative to <see cref="BaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Configurable because "/" is a login page on some hosts and a 404 on others, and a test that
    /// reports 404 for a working credential teaches an operator to ignore it.
    /// </remarks>
    public string ProbePath { get; set; } = "/";

    public DateTime? LastTestedAt { get; set; }

    /// <summary>The status code and round trip of the last test. Never a response body.</summary>
    public string? LastTestResult { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ConnectorAuth
{
    None,
    Basic,
    BearerToken,
    ApiKeyHeader,
    OAuth2ClientCredentials,
}

/// <summary>
/// One encrypted credential belonging to a <see cref="Connector"/>.
/// </summary>
/// <remarks>
/// A document of its own rather than a field, so the object the API returns has nowhere to carry a
/// secret. Nothing reads these except the sender, and the sender attaches them after the request
/// body has been composed, so a template can never resolve one.
/// </remarks>
public class ConnectorSecret
{
    public Guid Id { get; set; }

    /// <summary>The connector this belongs to.</summary>
    public Guid ConnectorId { get; set; }

    /// <summary>Which credential this is: "ApiToken", "AuthToken", "ClientSecret".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>AES-GCM ciphertext. There is no property here that holds a plaintext.</summary>
    public string ProtectedValue { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
