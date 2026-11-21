using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// TOTP (RFC 6238, authenticator-app) second factor: enrollment, code verification with a small clock
/// window and replay protection, and single-use recovery codes. Secrets are stored encrypted via
/// <c>IMfaSecretProtector</c>; recovery codes are stored only as BCrypt hashes.
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

    /// <summary>
    /// Mints the short-lived challenge a client takes to <c>/api/auth/mfa/verify</c> once a first
    /// factor has passed and the account still owes a second. The token is signed by the host, so
    /// the signing key stays in core.
    /// </summary>
    /// <param name="userId">The account that passed the first factor.</param>
    /// <param name="firstFactor">How it passed (<c>password</c>, <c>code</c>, <c>link</c> or <c>social:&lt;provider&gt;</c>), recorded on the session verify starts.</param>
    /// <param name="grantTenantSlug">The tenant an external sign-in's roles apply in. Null with no grant.</param>
    /// <param name="grantSource">Names where the roles came from (the provider). Ignored with no grant.</param>
    /// <param name="grantRoleIds">The roles to grant once the factor passes. Null or empty for no grant.</param>
    /// <returns>The signed token.</returns>
    /// <exception cref="NotSupportedException">The implementation predates this member and does not mint challenge tokens.</exception>
    /// <remarks>
    /// A default member, so an implementation compiled against an earlier contract keeps loading. It
    /// throws, because answering with something else would hand back a challenge verify rejects.
    /// The implementation core registers overrides it.
    /// </remarks>
    string CreateChallengeToken(
        Guid userId,
        string? firstFactor,
        string? grantTenantSlug = null,
        string? grantSource = null,
        IReadOnlyList<Guid>? grantRoleIds = null)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement CreateChallengeToken, so it cannot start a second-factor challenge.");
}
