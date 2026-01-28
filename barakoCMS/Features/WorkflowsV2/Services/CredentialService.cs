using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.WorkflowsV2.Models;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Service for managing workflow credentials (OAuth2, API keys, etc.).
/// </summary>
public class CredentialService : ICredentialService
{
    private readonly IDocumentSession _session;
    private readonly ILogger<CredentialService> _logger;
    private readonly CredentialEncryptionSettings _settings;

    public CredentialService(
        IDocumentSession session,
        IOptions<CredentialEncryptionSettings> settings,
        ILogger<CredentialService> logger)
    {
        _session = session;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ActionCredential?> GetCredentialAsync(string name, CancellationToken ct = default)
    {
        var credential = await _session.Query<ActionCredential>()
            .FirstOrDefaultAsync(c => c.Name == name, ct);

        return credential;
    }

    public async Task<List<ActionCredential>> ListCredentialsAsync(CancellationToken ct = default)
    {
        var credentials = await _session.Query<ActionCredential>()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        return credentials.ToList();
    }

    public async Task SaveCredentialAsync(ActionCredential credential, CancellationToken ct = default)
    {
        credential.UpdatedAt = DateTime.UtcNow;

        _session.Store(credential);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Saved credential: {Name}", credential.Name);
    }

    /// <summary>
    /// Save credential with data that will be encrypted.
    /// </summary>
    public async Task SaveCredentialWithDataAsync(
        ActionCredential credential,
        CredentialData data,
        CancellationToken ct = default)
    {
        // Encrypt and store the credential data
        var json = JsonSerializer.Serialize(data);
        credential.EncryptedData = Encrypt(json);
        credential.UpdatedAt = DateTime.UtcNow;

        _session.Store(credential);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Saved credential with encrypted data: {Name}", credential.Name);
    }

    /// <summary>
    /// Get decrypted credential data.
    /// </summary>
    public CredentialData? GetDecryptedData(ActionCredential credential)
    {
        if (string.IsNullOrEmpty(credential.EncryptedData))
            return null;

        try
        {
            var json = Decrypt(credential.EncryptedData);
            return JsonSerializer.Deserialize<CredentialData>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt credential data for {Name}", credential.Name);
            return null;
        }
    }

    public async Task DeleteCredentialAsync(string name, CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(name, ct);
        if (credential != null)
        {
            _session.Delete(credential);
            await _session.SaveChangesAsync(ct);
            _logger.LogInformation("Deleted credential: {Name}", name);
        }
    }

    public async Task<string?> GetAccessTokenAsync(string credentialName, CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(credentialName, ct);

        if (credential == null)
            return null;

        var data = GetDecryptedData(credential);
        if (data == null)
            return null;

        switch (credential.Type)
        {
            case CredentialType.OAuth2ClientCredentials:
            case CredentialType.OAuth2AuthorizationCode:
                return await GetOAuth2TokenAsync(credential, data, ct);

            case CredentialType.ApiKey:
                return data.ApiKey;

            case CredentialType.Bearer:
                return data.AccessToken;

            case CredentialType.Basic:
                if (!string.IsNullOrEmpty(data.Username) && !string.IsNullOrEmpty(data.Password))
                {
                    var basicAuth = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{data.Username}:{data.Password}"));
                    return $"Basic {basicAuth}";
                }
                break;
        }

        return null;
    }

    private async Task<string?> GetOAuth2TokenAsync(
        ActionCredential credential,
        CredentialData data,
        CancellationToken ct)
    {
        // Check if current token is still valid
        if (!string.IsNullOrEmpty(data.AccessToken) &&
            credential.ExpiresAt > DateTime.UtcNow.AddMinutes(5))
        {
            return data.AccessToken;
        }

        // Need to refresh the token
        if (!string.IsNullOrEmpty(data.RefreshToken))
        {
            var newToken = await RefreshOAuth2TokenAsync(credential, data, ct);
            if (newToken != null)
            {
                return newToken;
            }
        }

        // No valid token available
        _logger.LogWarning("OAuth2 credential {Name} has no valid token and cannot refresh", credential.Name);
        return null;
    }

    private async Task<string?> RefreshOAuth2TokenAsync(
        ActionCredential credential,
        CredentialData data,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data.TokenUrl) || string.IsNullOrEmpty(data.RefreshToken))
            return null;

        try
        {
            using var client = new HttpClient();

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = data.RefreshToken,
                ["client_id"] = data.ClientId ?? "",
                ["client_secret"] = data.ClientSecret ?? ""
            });

            var response = await client.PostAsync(data.TokenUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to refresh OAuth2 token for {Name}: {StatusCode}",
                    credential.Name, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<OAuth2TokenResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (tokenResponse == null)
                return null;

            // Update credential with new tokens
            data.AccessToken = tokenResponse.AccessToken;
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                data.RefreshToken = tokenResponse.RefreshToken;
            }

            credential.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);
            credential.LastRefreshedAt = DateTime.UtcNow;

            // Re-encrypt and save
            var dataJson = JsonSerializer.Serialize(data);
            credential.EncryptedData = Encrypt(dataJson);
            credential.UpdatedAt = DateTime.UtcNow;

            _session.Store(credential);
            await _session.SaveChangesAsync(ct);

            _logger.LogInformation("Refreshed OAuth2 token for {Name}", credential.Name);

            return tokenResponse.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing OAuth2 token for {Name}", credential.Name);
            return null;
        }
    }

    public async Task<string> InitiateOAuth2FlowAsync(
        string credentialName,
        string redirectUri,
        CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(credentialName, ct);

        if (credential == null)
            throw new ArgumentException($"Credential {credentialName} not found");

        var data = GetDecryptedData(credential);
        if (data == null || string.IsNullOrEmpty(data.AuthorizationUrl))
            throw new ArgumentException($"Credential {credentialName} is not configured for OAuth2");

        var state = Guid.NewGuid().ToString("N");

        // Store state and redirect URI in the credential data
        data.RedirectUri = redirectUri;
        var dataJson = JsonSerializer.Serialize(data);
        credential.EncryptedData = Encrypt(dataJson);

        // Store state in metadata (since we can't add properties to the model)
        // We'll use a temp storage approach - store state in Tags temporarily
        if (!credential.Tags.Contains($"state:{state}"))
        {
            credential.Tags.Add($"state:{state}");
        }

        _session.Store(credential);
        await _session.SaveChangesAsync(ct);

        // Build authorization URL
        var scopes = string.Join(" ", credential.Scopes);
        var authUrl = $"{data.AuthorizationUrl}?" +
            $"client_id={Uri.EscapeDataString(data.ClientId ?? "")}&" +
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
            $"response_type=code&" +
            $"scope={Uri.EscapeDataString(scopes)}&" +
            $"state={state}";

        return authUrl;
    }

    public async Task<bool> CompleteOAuth2FlowAsync(
        string credentialName,
        string code,
        string state,
        CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(credentialName, ct);

        if (credential == null)
            return false;

        // Verify state
        if (!credential.Tags.Contains($"state:{state}"))
        {
            _logger.LogWarning("OAuth2 state mismatch for {Name}", credentialName);
            return false;
        }

        var data = GetDecryptedData(credential);
        if (data == null || string.IsNullOrEmpty(data.TokenUrl))
            return false;

        try
        {
            using var client = new HttpClient();

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = data.RedirectUri ?? "",
                ["client_id"] = data.ClientId ?? "",
                ["client_secret"] = data.ClientSecret ?? ""
            });

            var response = await client.PostAsync(data.TokenUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to exchange OAuth2 code for {Name}: {StatusCode}",
                    credentialName, response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<OAuth2TokenResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (tokenResponse == null)
                return false;

            // Update credential with tokens
            data.AccessToken = tokenResponse.AccessToken;
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                data.RefreshToken = tokenResponse.RefreshToken;
            }

            credential.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);
            credential.LastRefreshedAt = DateTime.UtcNow;

            // Remove state tag
            credential.Tags.RemoveAll(t => t.StartsWith("state:"));

            // Re-encrypt and save
            var dataJson = JsonSerializer.Serialize(data);
            credential.EncryptedData = Encrypt(dataJson);
            credential.UpdatedAt = DateTime.UtcNow;
            credential.IsActive = true;

            _session.Store(credential);
            await _session.SaveChangesAsync(ct);

            _logger.LogInformation("Completed OAuth2 flow for {Name}", credentialName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing OAuth2 flow for {Name}", credentialName);
            return false;
        }
    }

    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(_settings.EncryptionKey);
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Combine IV and encrypted data
        var result = new byte[aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        var fullCipher = Convert.FromBase64String(cipherText);

        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(_settings.EncryptionKey);

        // Extract IV
        var iv = new byte[16];
        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        aes.IV = iv;

        // Extract encrypted data
        var encryptedBytes = new byte[fullCipher.Length - iv.Length];
        Buffer.BlockCopy(fullCipher, iv.Length, encryptedBytes, 0, encryptedBytes.Length);

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}

/// <summary>
/// Settings for credential encryption.
/// </summary>
public class CredentialEncryptionSettings
{
    /// <summary>
    /// Base64-encoded 256-bit AES encryption key.
    /// </summary>
    public string EncryptionKey { get; set; } = "";
}
