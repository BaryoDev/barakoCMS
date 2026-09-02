using System.Security.Cryptography;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// The token a registrant is emailed and hands back to prove they read the mailbox.
/// </summary>
/// <remarks>
/// Shape is <c>{pendingRegistrationId:N}.{secret}</c>, where the secret is 32 random bytes in
/// base64url. Only the secret's BCrypt hash is stored, which is the same at-rest rule
/// <c>OtpCode.CodeHash</c> and <c>MfaSecret.RecoveryCodeHashes</c> already follow. A BCrypt hash
/// cannot be looked up by, hence the id travelling alongside: load one row by id, then verify.
///
/// There is no attempt cap on this one, and that is not the oversight it looks like next to
/// <c>OtpCode</c>. A 6-digit code has about 20 bits and a cap is the only thing between it and a
/// laptop; a 256-bit secret is not guessed. The rate limit on the endpoint is there for the traffic,
/// not for the search space.
/// </remarks>
public static class EmailVerificationToken
{
    private const int SecretBytes = 32;

    /// <summary>A fresh token and the hash to store for it.</summary>
    public static (string Token, string Hash) Create(Guid pendingRegistrationId)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        return ($"{pendingRegistrationId:N}.{secret}", BCrypt.Net.BCrypt.HashPassword(secret));
    }

    /// <summary>
    /// Splits a token into the row to load and the secret to verify. False for anything malformed,
    /// which the caller must answer exactly as it answers a wrong secret.
    /// </summary>
    public static bool TryParse(string? token, out Guid pendingRegistrationId, out string secret)
    {
        pendingRegistrationId = Guid.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(token[..dot], "N", out pendingRegistrationId))
        {
            return false;
        }

        secret = token[(dot + 1)..];
        return true;
    }

    public static bool Matches(string secret, string hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(secret, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A stored hash that is not a BCrypt hash is a refusal, not a 500. Login next door
            // learned this the hard way with an empty PasswordHash on social-created accounts.
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
