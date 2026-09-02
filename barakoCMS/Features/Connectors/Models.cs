using barakoCMS.Models;

namespace barakoCMS.Features.Connectors;

/// <summary>
/// A connector as the API describes it.
/// </summary>
/// <remarks>
/// There is no field here that could hold a secret, and that is the design rather than an omission.
/// Secrets live in their own document and the read path never joins them, so a bug that returns this
/// object cannot leak a token: there is nothing in it to leak. `SecretKeys` carries the names only,
/// which is what an admin screen needs to say "an ApiToken is set" without ever handling one.
/// </remarks>
internal sealed class ConnectorResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string Auth { get; init; } = nameof(ConnectorAuth.None);
    public Dictionary<string, string> Settings { get; init; } = new();
    public List<string> SecretKeys { get; init; } = new();
    public bool Enabled { get; init; }
    public string ProbePath { get; init; } = "/";
    public DateTime? LastTestedAt { get; init; }
    public string? LastTestResult { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static ConnectorResponse From(Connector c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Slug = c.Slug,
        BaseUrl = c.BaseUrl,
        Auth = c.Auth.ToString(),
        Settings = c.Settings,
        SecretKeys = c.SecretKeys,
        Enabled = c.Enabled,
        ProbePath = c.ProbePath,
        LastTestedAt = c.LastTestedAt,
        LastTestResult = c.LastTestResult,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}

internal class SaveConnectorRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Auth { get; set; } = nameof(ConnectorAuth.None);
    public Dictionary<string, string> Settings { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string ProbePath { get; set; } = "/";

    /// <summary>
    /// Credentials to store, keyed by name. Write only: nothing ever reads these back out.
    /// </summary>
    /// <remarks>
    /// On update, a key that is absent leaves the stored secret alone and an empty value removes it.
    /// They have to mean different things, because the screen cannot show the current value and so
    /// has no way to send it back unchanged: an absent key meaning "delete" would wipe the token
    /// every time somebody corrected the base URL.
    /// </remarks>
    public Dictionary<string, string>? Secrets { get; set; }
}

internal sealed class TestConnectorResponse
{
    public bool Succeeded { get; init; }
    public int? StatusCode { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
}
