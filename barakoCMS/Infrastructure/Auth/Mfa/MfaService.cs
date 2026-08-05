using System.Security.Cryptography;
using barakoCMS.Models;
using Marten;
using OtpNet;

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

public sealed class MfaService : IMfaService
{
    private const int RecoveryCodeCount = 10;
    private const int SecretBytes = 20; // 160-bit TOTP secret
    // Allow one step either side (~±30s) for clock drift between the server and the authenticator.
    private static readonly VerificationWindow Window = new(previous: 1, future: 1);

    private readonly IDocumentSession _session;
    private readonly IMfaSecretProtector _protector;
    private readonly IConfiguration _config;

    public MfaService(IDocumentSession session, IMfaSecretProtector protector, IConfiguration config)
    {
        _session = session;
        _protector = protector;
        _config = config;
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct)
    {
        var mfa = await _session.LoadAsync<MfaSecret>(userId, ct);
        return mfa is { Enabled: true };
    }

    public async Task<(string Secret, string OtpauthUri)> BeginSetupAsync(User user, CancellationToken ct)
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(SecretBytes);
        var secret = Base32Encoding.ToString(secretBytes);

        // Load-then-update so the optimistic-concurrency version is tracked: calling setup again while a
        // pending enrollment exists replaces it cleanly instead of colliding on the version.
        var mfa = await _session.LoadAsync<MfaSecret>(user.Id, ct) ?? new MfaSecret { Id = user.Id };
        mfa.EncryptedSecret = _protector.Protect(secret);
        mfa.Enabled = false;
        mfa.RecoveryCodeHashes = new();
        mfa.LastUsedTimeStep = 0;
        mfa.CreatedAt = DateTime.UtcNow;
        mfa.ConfirmedAt = null;
        _session.Store(mfa);
        await _session.SaveChangesAsync(ct);

        return (secret, BuildOtpauthUri(user.Username, secret));
    }

    public async Task<IReadOnlyList<string>?> ConfirmSetupAsync(Guid userId, string code, CancellationToken ct)
    {
        var mfa = await _session.LoadAsync<MfaSecret>(userId, ct);
        if (mfa is null || mfa.Enabled) return null;

        // Confirming enrollment does not arm the replay guard, so the user can immediately sign in with
        // the next code from the same 30s window. The guard is armed on login/disable instead.
        if (!TryConsumeTotp(mfa, code, advanceReplayGuard: false)) return null;

        var (plain, hashes) = GenerateRecoveryCodes();
        mfa.Enabled = true;
        mfa.ConfirmedAt = DateTime.UtcNow;
        mfa.RecoveryCodeHashes = hashes;
        _session.Update(mfa);
        await _session.SaveChangesAsync(ct);
        return plain;
    }

    public async Task<bool> VerifyCodeAsync(Guid userId, string code, CancellationToken ct)
    {
        var mfa = await _session.LoadAsync<MfaSecret>(userId, ct);
        if (mfa is null || !mfa.Enabled) return false;

        if (TryConsumeTotp(mfa, code, advanceReplayGuard: true) || TryConsumeRecoveryCode(mfa, code))
        {
            _session.Update(mfa);
            try
            {
                await _session.SaveChangesAsync(ct);
            }
            catch (JasperFx.ConcurrencyException)
            {
                // A concurrent verify already advanced the guard / consumed the code. Fail this one
                // rather than double-accepting the same code.
                return false;
            }
            return true;
        }
        return false;
    }

    public async Task<bool> DisableAsync(Guid userId, string code, CancellationToken ct)
    {
        var mfa = await _session.LoadAsync<MfaSecret>(userId, ct);
        if (mfa is null || !mfa.Enabled) return false;

        if (!TryConsumeTotp(mfa, code, advanceReplayGuard: true) && !TryConsumeRecoveryCode(mfa, code)) return false;

        _session.Delete(mfa);
        await _session.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Verifies a TOTP against the stored secret and, on success, advances the replay guard so the same
    /// code (time-step) cannot be used twice. Mutates <paramref name="mfa"/> in memory only.
    /// </summary>
    private bool TryConsumeTotp(MfaSecret mfa, string code, bool advanceReplayGuard)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length == 0) return false;

        var secret = _protector.Unprotect(mfa.EncryptedSecret);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        if (!totp.VerifyTotp(code, out var matchedStep, Window)) return false;

        if (advanceReplayGuard)
        {
            // Reject a code from a step we've already accepted (replay within the window).
            if (matchedStep <= mfa.LastUsedTimeStep) return false;
            mfa.LastUsedTimeStep = matchedStep;
        }
        return true;
    }

    private static bool TryConsumeRecoveryCode(MfaSecret mfa, string code)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length == 0 || mfa.RecoveryCodeHashes.Count == 0) return false;

        // Normalize display grouping ("xxxx-xxxx" -> "xxxxxxxx") before comparing.
        var normalized = code.Replace("-", string.Empty);

        var match = mfa.RecoveryCodeHashes.FirstOrDefault(h => BCrypt.Net.BCrypt.Verify(normalized, h));
        if (match is null) return false;

        mfa.RecoveryCodeHashes.Remove(match); // single-use
        return true;
    }

    private static (IReadOnlyList<string> Plain, List<string> Hashes) GenerateRecoveryCodes()
    {
        var plain = new List<string>(RecoveryCodeCount);
        var hashes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            // 40 bits -> 8 base32 chars, shown grouped as xxxx-xxxx. Stored only as a BCrypt hash.
            var raw = Base32Encoding.ToString(RandomNumberGenerator.GetBytes(5)).TrimEnd('=').ToLowerInvariant();
            var normalized = raw[..Math.Min(8, raw.Length)];
            plain.Add($"{normalized[..4]}-{normalized[4..]}");
            hashes.Add(BCrypt.Net.BCrypt.HashPassword(normalized));
        }
        return (plain, hashes);
    }

    private string BuildOtpauthUri(string username, string secret)
    {
        var issuer = _config["Branding:AppName"] ?? "BarakoCMS";
        var label = Uri.EscapeDataString($"{issuer}:{username}");
        var query = $"secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
        return $"otpauth://totp/{label}?{query}";
    }
}
