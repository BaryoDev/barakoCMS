namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Stored credentials for workflow actions (OAuth2, API keys, etc.).
/// </summary>
public class ActionCredential
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for this credential.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this credential is used for.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Type of credential.
    /// </summary>
    public CredentialType Type { get; set; }

    /// <summary>
    /// Service/provider this credential is for (e.g., "slack", "sendgrid", "custom").
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted credential data.
    /// </summary>
    public string EncryptedData { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 specific: Token expiry time.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// OAuth2 specific: Required scopes.
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Whether this credential is active and usable.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Last time this credential was used.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Last time this credential was refreshed (for OAuth2).
    /// </summary>
    public DateTime? LastRefreshedAt { get; set; }

    /// <summary>
    /// Number of times this credential has been used.
    /// </summary>
    public int UsageCount { get; set; }

    /// <summary>
    /// Who created this credential.
    /// </summary>
    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tags for organization.
    /// </summary>
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Type of credential storage.
/// </summary>
public enum CredentialType
{
    /// <summary>
    /// Simple API key.
    /// </summary>
    ApiKey,

    /// <summary>
    /// Basic authentication (username/password).
    /// </summary>
    Basic,

    /// <summary>
    /// Bearer token.
    /// </summary>
    Bearer,

    /// <summary>
    /// OAuth2 client credentials flow.
    /// </summary>
    OAuth2ClientCredentials,

    /// <summary>
    /// OAuth2 authorization code flow.
    /// </summary>
    OAuth2AuthorizationCode,

    /// <summary>
    /// SMTP credentials.
    /// </summary>
    Smtp,

    /// <summary>
    /// Custom/other credential type.
    /// </summary>
    Custom
}

/// <summary>
/// Decrypted credential data structure.
/// </summary>
public class CredentialData
{
    // API Key
    public string? ApiKey { get; set; }
    public string? ApiKeyHeader { get; set; }

    // Basic Auth
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Bearer Token
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? TokenType { get; set; }

    // OAuth2
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? TokenUrl { get; set; }
    public string? RedirectUri { get; set; }

    // SMTP
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public bool? SmtpUseSsl { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }

    // Custom
    public Dictionary<string, string>? CustomFields { get; set; }
}

/// <summary>
/// OAuth2 token response.
/// </summary>
public class OAuth2TokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public int ExpiresIn { get; set; }
    public string? Scope { get; set; }
}
