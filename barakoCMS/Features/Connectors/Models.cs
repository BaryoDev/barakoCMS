using barakoCMS.Infrastructure.Connectors;
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

    /// <summary>Null for a connector with no inbound section.</summary>
    public InboundResponse? Inbound { get; init; }

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
        Inbound = c.Inbound is null
            ? null
            : new InboundResponse
            {
                Scheme = c.Inbound.Scheme,
                AllowedEvents = c.Inbound.AllowedEvents,
                SecretSet = c.SecretKeys.Contains(ConnectorSecretKeys.InboundSecret),
            },
        LastTestedAt = c.LastTestedAt,
        LastTestResult = c.LastTestResult,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}

/// <summary>The inbound section as the API describes it. The secret is only ever reported as set or not.</summary>
internal sealed class InboundResponse
{
    public string Scheme { get; init; } = "Hmac";
    public List<string> AllowedEvents { get; init; } = new();
    public bool SecretSet { get; init; }
}

/// <summary>The inbound section of a save.</summary>
internal sealed class SaveConnectorInbound
{
    /// <summary><c>Hmac</c> or <c>Svix</c>.</summary>
    public string Scheme { get; set; } = "Hmac";

    /// <summary>The provider event names this connector may send. Empty means any.</summary>
    public List<string>? AllowedEvents { get; set; }

    /// <summary>
    /// The inbound signing secret. Write only, and handled like <see cref="SaveConnectorRequest.Secrets"/>:
    /// absent keeps the stored one, an empty value removes it.
    /// </summary>
    public string? Secret { get; set; }
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

    /// <summary>
    /// How this party signs what it sends to us. Absent on update leaves the stored section alone.
    /// </summary>
    public SaveConnectorInbound? Inbound { get; set; }
}

internal sealed class TestConnectorResponse
{
    public bool Succeeded { get; init; }
    public int? StatusCode { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
}
