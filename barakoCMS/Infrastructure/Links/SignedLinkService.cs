using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using Marten;
using Microsoft.AspNetCore.WebUtilities;

namespace barakoCMS.Infrastructure.Links;

/// <summary>
/// A token is <c>payload~signature</c>, both base64url. The payload holds the purpose, subject, expiry,
/// token id and single use flag. The signature is HMAC-SHA256 over the payload with a key derived from
/// <c>JWT:Key</c> and the purpose. The separator is <c>~</c> so a link is never mistaken for a JWT.
/// </summary>
public sealed class SignedLinkService(IDocumentSession session, IConfiguration config, TimeProvider clock) : ISignedLinkService
{
    private const string KeyContext = "barakocms/signed-link/v1/";

    private sealed record Payload(string P, string S, long E, string I, bool U);

    public SignedLink Issue(string purpose, string subject, TimeSpan lifetime, bool singleUse = false)
    {
        if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A purpose is required.", nameof(purpose));
        ArgumentNullException.ThrowIfNull(subject);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime), "A link must last longer than zero.");

        var expiresAt = clock.GetUtcNow().Add(lifetime);
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new Payload(purpose, subject, expiresAt.ToUnixTimeSeconds(), id, singleUse)));
        var signature = WebEncoders.Base64UrlEncode(Sign(purpose, payload) ?? throw new InvalidOperationException("JWT:Key is not configured."));
        return new SignedLink($"{payload}~{signature}", id, DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds()));
    }

    public async Task<SignedLinkResult> ValidateAsync(string purpose, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(purpose) || string.IsNullOrWhiteSpace(token)) return Fail(SignedLinkFailure.Invalid);

        var parts = token.Split('~');
        if (parts.Length != 2) return Fail(SignedLinkFailure.Invalid);

        Payload? payload;
        try
        {
            var expected = Sign(purpose, parts[0]);
            if (expected is null) return Fail(SignedLinkFailure.Invalid);
            var given = WebEncoders.Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expected, given)) return Fail(SignedLinkFailure.Invalid);
            payload = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(parts[0]));
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return Fail(SignedLinkFailure.Invalid);
        }

        if (payload is null || payload.P != purpose || string.IsNullOrEmpty(payload.I) || payload.S is null)
            return Fail(SignedLinkFailure.Invalid);
        if (clock.GetUtcNow().ToUnixTimeSeconds() >= payload.E) return Fail(SignedLinkFailure.Expired);

        var use = await session.LoadAsync<SignedLinkUse>(payload.I, ct);
        if (use is { Revoked: true }) return Fail(SignedLinkFailure.Revoked);
        if (use is { Redeemed: true }) return Fail(SignedLinkFailure.AlreadyUsed);

        if (payload.U)
        {
            session.Insert(new SignedLinkUse { Id = payload.I, Redeemed = true, UpdatedAt = clock.GetUtcNow() });
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (JasperFx.DocumentAlreadyExistsException)
            {
                return Fail(SignedLinkFailure.AlreadyUsed);
            }
        }

        return new SignedLinkResult(payload.S, SignedLinkFailure.None);
    }

    public async Task RevokeAsync(string tokenId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId)) throw new ArgumentException("A token id is required.", nameof(tokenId));
        var use = await session.LoadAsync<SignedLinkUse>(tokenId, ct) ?? new SignedLinkUse { Id = tokenId };
        use.Revoked = true;
        use.UpdatedAt = clock.GetUtcNow();
        session.Store(use);
        await session.SaveChangesAsync(ct);
    }

    private static SignedLinkResult Fail(SignedLinkFailure failure) => new(null, failure);

    private byte[]? Sign(string purpose, string payload)
    {
        var secret = config["JWT:Key"];
        if (string.IsNullOrEmpty(secret)) return null;
        var purposeKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(KeyContext + purpose));
        return HMACSHA256.HashData(purposeKey, Encoding.UTF8.GetBytes(payload));
    }
}
