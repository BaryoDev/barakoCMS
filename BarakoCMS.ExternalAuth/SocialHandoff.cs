using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// A sign-in result waiting to be collected by a server-side frontend with
/// <c>POST /api/auth/social/exchange</c>. Global, like the user it signs in.
/// </summary>
/// <remarks>
/// The id is the SHA-256 of the code, so the code itself is not stored. The tokens are encrypted
/// with a key derived from the code, which only the browser holds, so a copy of the table opens
/// nothing. <see cref="DeviceHash"/> is the SHA-256 of the cookie the start route set. The table
/// uses optimistic concurrency: two exchanges both read <see cref="Used"/> as false, and only the
/// first save of the new version commits.
/// </remarks>
public sealed class SocialHandoffCode
{
    public string Id { get; set; } = string.Empty;

    public string DeviceHash { get; set; } = string.Empty;

    /// <summary>The encrypted outcome. Empty once the code is used.</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public bool Used { get; set; }
}

/// <summary>What the exchange hands back: the tokens, or the second factor still owed.</summary>
internal sealed record SocialHandoffOutcome(
    string Token, string Refresh, bool RequiresMfa, string? MfaChallenge, string Club);

/// <summary>The code handoff: a short-lived single-use code in the redirect instead of tokens.</summary>
/// <remarks>
/// Off unless <c>ExternalAuth:CodeHandoff</c> is true, so the redirect with the tokens in the
/// fragment stays the default. The device is the browser that started the sign-in: the start route
/// sets a random cookie, and the exchange must present it. A frontend that exchanges from its own
/// server forwards that cookie. Starting stores nothing. A code is only stored once a provider has
/// vouched for a sign-in, it lives <see cref="Lifetime"/>, expired rows are purged in bounded
/// batches, and no more than <see cref="MaxLive"/> live codes exist at once.
/// </remarks>
internal static class SocialHandoff
{
    public const string DeviceCookie = "social_device";
    public const string ConfigKey = "ExternalAuth:CodeHandoff";
    public const int MaxLive = 5000;
    public const int MaxCodeLength = 128;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(1);
    internal const int PurgeBatchSize = 500;
    internal const int PurgeBatches = 10;

    private static long _nextPurgeTicks;

    public static bool Enabled(IConfiguration config) => config.GetValue<bool>(ConfigKey);

    /// <summary>Ties the sign-in to this browser. A start route calls it before redirecting to the provider.</summary>
    public static void BindDevice(HttpContext http, IConfiguration config, CookieOptions? options = null)
    {
        if (!Enabled(config))
        {
            return;
        }

        http.Response.Cookies.Append(DeviceCookie, NewSecret(), options ?? ExternalAuthSupport.ShortCookie());
    }

    internal static CookieOptions CookieForRemoval() => new()
    {
        HttpOnly = true,
        Secure = true,
        Path = "/",
    };

    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>The stored form of an outcome, for tests and the issuer alike.</summary>
    internal static SocialHandoffCode Build(string code, string device, SocialHandoffOutcome outcome, DateTime expiresAt) =>
        new()
        {
            Id = HashOf(code),
            DeviceHash = HashOf(device),
            Payload = Seal(code, outcome),
            ExpiresAt = expiresAt,
        };

    /// <summary>
    /// Stores the outcome under a new code and returns the frontend URL that carries only the code.
    /// </summary>
    public static async Task<string> RedirectAsync(
        IDocumentSession session, HttpContext http, string baseUrl, SocialHandoffOutcome outcome, CancellationToken ct)
    {
        var device = http.Request.Cookies[DeviceCookie];
        if (string.IsNullOrEmpty(device) || device.Length > MaxCodeLength)
        {
            return SocialSignIn.FrontendError(baseUrl, "Sign-in expired. Please try again.", outcome.Club);
        }

        await PurgeIfDueAsync(http.RequestServices.GetRequiredService<IDocumentStore>(), http.RequestServices, ct);
        if (!await HasRoomAsync(session, MaxLive, ct))
        {
            return SocialSignIn.FrontendError(baseUrl, "Sign-in is busy. Please try again in a minute.", outcome.Club);
        }

        var code = NewSecret();
        session.Insert(Build(code, device, outcome, DateTime.UtcNow + Lifetime));
        await session.SaveChangesAsync(ct);
        return $"{baseUrl}/auth/social?code={Uri.EscapeDataString(code)}";
    }

    /// <summary>Whether fewer than <paramref name="max"/> unexpired, unused codes exist.</summary>
    internal static async Task<bool> HasRoomAsync(IQuerySession session, int max, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var live = await session.Query<SocialHandoffCode>().CountAsync(c => c.ExpiresAt > now && !c.Used, ct);
        return live < max;
    }

    internal static string Seal(string code, SocialHandoffOutcome outcome)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(outcome);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(KeyOf(code), 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    internal static SocialHandoffOutcome? Open(string code, string payload)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length < 28)
            {
                return null;
            }

            var plain = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(KeyOf(code), 16))
            {
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            }

            return JsonSerializer.Deserialize<SocialHandoffOutcome>(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static byte[] KeyOf(string code) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), "barako-social-handoff-v1"u8);

    /// <summary>Runs <see cref="PurgeAsync"/> when the interval has passed, on one request at a time.</summary>
    internal static async Task PurgeIfDueAsync(IDocumentStore store, IServiceProvider services, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = Interlocked.Read(ref _nextPurgeTicks);
        if (now.Ticks < due
            || Interlocked.CompareExchange(ref _nextPurgeTicks, (now + PurgeInterval).Ticks, due) != due)
        {
            return;
        }

        try
        {
            await PurgeAsync(store, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sign-in does not fail because the cleanup did. The next interval tries again.
            services.GetService<ILoggerFactory>()?.CreateLogger("BarakoCMS.ExternalAuth.SocialHandoff")
                .LogWarning(ex, "Expired social handoff codes could not be purged");
        }
    }

    /// <summary>Deletes expired codes, a bounded number at a time. Returns how many went.</summary>
    internal static async Task<int> PurgeAsync(IDocumentStore store, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var removed = 0;
        for (var batch = 0; batch < PurgeBatches; batch++)
        {
            await using var session = store.LightweightSession();
            var expired = await session.Query<SocialHandoffCode>()
                .Where(c => c.ExpiresAt <= now)
                .Select(c => c.Id)
                .Take(PurgeBatchSize)
                .ToListAsync(ct);
            if (expired.Count == 0)
            {
                return removed;
            }

            var ids = expired.ToList();
            session.DeleteWhere<SocialHandoffCode>(c => ids.Contains(c.Id));
            await session.SaveChangesAsync(ct);
            removed += expired.Count;
            if (expired.Count < PurgeBatchSize)
            {
                return removed;
            }
        }

        return removed;
    }
}
