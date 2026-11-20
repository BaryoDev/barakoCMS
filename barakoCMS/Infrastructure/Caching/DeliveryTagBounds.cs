namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// How many cache tags a delivery response carries and how long each tag header may be. The
/// defaults are the bounds that shipped in 5.8.0. A deployment raises them with
/// <c>Delivery:CacheTags:MaxTags</c> and <c>Delivery:CacheTags:MaxHeaderLength</c> (#561).
/// </summary>
/// <remarks>
/// Both headers (<c>Surrogate-Key</c> and <c>Cache-Tag</c>) carry the same value, so a raised
/// header length costs twice its size in response headers, and a proxy in front must accept it.
/// Values are clamped, never fatal: <see cref="Read"/> says what it changed.
/// </remarks>
/// <param name="MaxTags">Tags kept per response, at least 1 so the tenant tag always fits.</param>
/// <param name="MaxHeaderLength">Bytes per tag header, at least the default of 1024.</param>
public sealed record DeliveryTagBounds(int MaxTags, int MaxHeaderLength)
{
    public const string MaxTagsKey = "Delivery:CacheTags:MaxTags";
    public const string MaxHeaderLengthKey = "Delivery:CacheTags:MaxHeaderLength";

    /// <summary>Fewest tags a response may be bounded to: the tenant tag.</summary>
    public const int MinTags = 1;

    /// <summary>
    /// Most tags a deployment may ask for. Cloudflare says its 16 KB limit is about 1,000 unique
    /// tags; Fastly states no count.
    /// </summary>
    public const int CeilingTags = 1000;

    /// <summary>
    /// Most bytes one tag header may be: both Cloudflare <c>Cache-Tag</c> and Fastly
    /// <c>Surrogate-Key</c> refuse a header past 16 KB (16,384 bytes).
    /// </summary>
    public const int CeilingHeaderLength = 16384;

    /// <summary>The bounds a deployment that sets nothing gets: 32 tags, 1024 bytes.</summary>
    public static DeliveryTagBounds Default { get; } = new(DeliveryCache.MaxTags, DeliveryCache.MaxTagHeaderLength);

    /// <summary>Reads both keys, clamps what is out of range, and returns a line per change.</summary>
    public static DeliveryTagBounds Read(IConfiguration configuration, out IReadOnlyList<string> warnings)
    {
        var notes = new List<string>();
        var tags = Clamp(configuration, MaxTagsKey, DeliveryCache.MaxTags, MinTags, CeilingTags, notes);
        var length = Clamp(configuration, MaxHeaderLengthKey, DeliveryCache.MaxTagHeaderLength,
            DeliveryCache.MaxTagHeaderLength, CeilingHeaderLength, notes);
        warnings = notes;
        return new DeliveryTagBounds(tags, length);
    }

    private static int Clamp(
        IConfiguration configuration, string key, int fallback, int floor, int ceiling, List<string> notes)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            notes.Add($"{key} is '{raw}', which is not a whole number, so the default {fallback} is used.");
            return fallback;
        }

        var used = Math.Clamp(value, floor, ceiling);
        if (used != value)
            notes.Add($"{key} is {value}, outside {floor} to {ceiling}, so {used} is used.");
        return used;
    }
}

/// <summary>Logs, once at start, any <c>Delivery:CacheTags</c> value that was clamped.</summary>
internal sealed class DeliveryTagBoundsCheck(
    IConfiguration configuration, ILogger<DeliveryTagBoundsCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        DeliveryTagBounds.Read(configuration, out var warnings);
        foreach (var warning in warnings)
            logger.LogWarning("{Warning}", warning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
