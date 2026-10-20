using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// TOTP (RFC 6238, authenticator-app) second factor: enrollment, code verification with a small clock
/// window and replay protection, and single-use recovery codes. Secrets are stored encrypted via
/// <see cref="IMfaSecretProtector"/>; recovery codes are stored only as BCrypt hashes.
/// </summary>
public interface IMfaService
{
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct);

    /// <summary>Creates (or replaces) a pending enrollment and returns the secret + otpauth URI to show once.</summary>
    Task<(string Secret, string OtpauthUri)> BeginSetupAsync(User user, CancellationToken ct);

    /// <summary>Confirms a pending enrollment with a code. Returns the recovery codes on success, null on a bad code.</summary>
    Task<IReadOnlyList<string>?> ConfirmSetupAsync(Guid userId, string code, CancellationToken ct);

    /// <summary>Verifies a TOTP or recovery code for an enabled account (used at login step-up).</summary>
    Task<bool> VerifyCodeAsync(Guid userId, string code, CancellationToken ct);

    /// <summary>Disables MFA after verifying a current TOTP or recovery code. Returns false on a bad code.</summary>
    Task<bool> DisableAsync(Guid userId, string code, CancellationToken ct);
}
