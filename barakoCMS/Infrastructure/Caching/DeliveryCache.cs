using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Caching;

/// <summary>How long a delivery read may be kept, said apart from the Cache-Control hint (#973).</summary>
public enum DeliveryCacheClass
{
    /// <summary>Minutes. Today's <c>public, max-age=60</c> reads.</summary>
    Short,

    /// <summary>Hours or days. A public file, whose bytes never change for one stored record.</summary>
    Long,

    /// <summary>Never kept. A draft preview, or anything else built for one viewer.</summary>
    NoStore,

    /// <summary>May be served stale while it is fetched again. No route sends it by default.</summary>
    StaleWhileRevalidate,
}

/// <summary>One part of a cache tag after the tenant, such as <c>type:post</c> or <c>entry:&lt;id&gt;</c>.</summary>
/// <remarks>
/// Only what a public response already shows goes in a scope: a type name, a published entry's id,
/// a public file's id, or a fixed word. Never a value that is not delivered or that differs per caller.
/// </remarks>
public readonly record struct CacheScope(string Kind, string? Value = null)
{
    public static CacheScope Type(string type) => new("type", type);

    public static CacheScope Entry(Guid id) => new("entry", id.ToString("D"));

    public static CacheScope Named(string kind) => new(kind);
}

/// <summary>Marker on an endpoint whose 200 is hashed into an ETag and answered 304 on a match.</summary>
public sealed class DeliveryValidatorsMetadata
{
    internal DeliveryValidatorsMetadata() { }
}

/// <summary>
/// The cache class, tags and validators every delivery read sends (#973, #561).
/// </summary>
/// <remarks>
/// <para>
/// Cache-Control is left to each route and keeps the value it had. The class is a separate header
/// so a renderer can read how long a read may be kept without taking the CDN hint as a lifetime.
/// </para>
/// <para>
/// Tags go out under both spellings, <c>Surrogate-Key</c> (Fastly) and <c>Cache-Tag</c>
/// (Cloudflare), each one space-separated value. Every tag starts with <c>t:&lt;tenant&gt;</c>, so a
/// purge by tag cannot cross a tenant. At most <see cref="MaxTags"/> tags and
/// <see cref="MaxTagHeaderLength"/> bytes per header, which keeps both headers inside the 4 KB
/// header buffer a default nginx proxy allows. Tags past either bound are dropped in order, the
/// tenant and type tags come first so they survive, and the count dropped is sent in
/// <see cref="TagsDroppedHeader"/>. A response that lost entry tags is still purged by its type tag.
/// </para>
/// <para>
/// The ETag is weak: <c>W/"..."</c> over the tenant and the exact body sent. A proxy that compresses
/// the body changes its bytes but not its meaning, nginx turns a strong tag weak when it gzips
/// anyway, and no delivery route serves ranges, which is the one thing a strong tag is needed for.
/// If-None-Match uses weak comparison, so a match answers 304 either way.
/// </para>
/// </remarks>
public static class DeliveryCache
{
    public const string ClassHeader = "X-Barako-Cache-Class";
    public const string SurrogateKeyHeader = "Surrogate-Key";
    public const string CacheTagHeader = "Cache-Tag";
    public const string TagsDroppedHeader = "X-Barako-Cache-Tags-Dropped";

    public const int MaxTags = 32;
    public const int MaxTagHeaderLength = 1024;

    /// <summary>Add to an endpoint's <c>Options</c> so its 200 gets an ETag and a 304 on a match.</summary>
    public static readonly DeliveryValidatorsMetadata Validators = new();

    public static string Name(DeliveryCacheClass cacheClass) => cacheClass switch
    {
        DeliveryCacheClass.Short => "short",
        DeliveryCacheClass.Long => "long",
        DeliveryCacheClass.NoStore => "no-store",
        DeliveryCacheClass.StaleWhileRevalidate => "swr",
        _ => throw new ArgumentOutOfRangeException(nameof(cacheClass)),
    };

    /// <summary>
    /// Marks a response a shared cache may keep: the class, the tenant's tags, and Last-Modified when
    /// the data has a timestamp. Cache-Control is not touched.
    /// </summary>
    public static void Shared(
        HttpContext http,
        DeliveryCacheClass cacheClass,
        IEnumerable<CacheScope> scopes,
        DateTimeOffset? lastModified = null)
    {
        var headers = http.Response.Headers;
        headers[ClassHeader] = Name(cacheClass);

        var (value, dropped) = TagHeaderValue(TenantOf(http), scopes);
        if (value.Length > 0)
        {
            headers[SurrogateKeyHeader] = value;
            headers[CacheTagHeader] = value;
        }
        if (dropped > 0)
            headers[TagsDroppedHeader] = dropped.ToString(CultureInfo.InvariantCulture);

        if (lastModified is { } at)
            headers.LastModified = Seconds(at).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Marks a response built for one viewer: <c>no-store</c>, and no tag or validator.</summary>
    public static void NoStore(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        headers[ClassHeader] = Name(DeliveryCacheClass.NoStore);
        headers.Remove(SurrogateKeyHeader);
        headers.Remove(CacheTagHeader);
        headers.Remove(TagsDroppedHeader);
        headers.Remove(HeaderNames.ETag);
        headers.Remove(HeaderNames.LastModified);
    }

    /// <summary>True when the response says a shared cache may keep it.</summary>
    public static bool IsShared(HttpResponse response)
    {
        var cacheClass = response.Headers[ClassHeader].ToString();
        if (cacheClass.Length == 0 || cacheClass == Name(DeliveryCacheClass.NoStore))
            return false;

        var cacheControl = response.Headers.CacheControl.ToString();
        return !cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase)
               && !cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase);
    }

    public static string TenantTag(string tenant) => "t:" + Escape(tenant);

    public static string Tag(string tenant, CacheScope scope) =>
        TenantTag(tenant) + ":" + Escape(scope.Kind) + (scope.Value is null ? string.Empty : ":" + Escape(scope.Value));

    /// <summary>The tag header value for these scopes, and how many tags the bounds dropped.</summary>
    public static (string Value, int Dropped) TagHeaderValue(string tenant, IEnumerable<CacheScope> scopes)
    {
        var builder = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = 0;
        var dropped = 0;

        foreach (var tag in scopes.Select(s => Tag(tenant, s)).Prepend(TenantTag(tenant)))
        {
            if (!seen.Add(tag))
                continue;

            var length = builder.Length + (builder.Length == 0 ? 0 : 1) + tag.Length;
            if (kept >= MaxTags || length > MaxTagHeaderLength)
            {
                dropped++;
                continue;
            }

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(tag);
            kept++;
        }

        return (builder.ToString(), dropped);
    }

    /// <summary>A weak ETag over the tenant and the bytes a caller is sent.</summary>
    public static string WeakETag(string tenant, ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(tenant));
        hash.AppendData([0]);
        hash.AppendData(body);
        var digest = hash.GetHashAndReset();
        return "W/\"" + Base64Url(digest.AsSpan(0, 16)) + "\"";
    }

    /// <summary>A weak ETag over the tenant and a description of what is served, for a route that does not buffer.</summary>
    public static string WeakETag(string tenant, string material) =>
        WeakETag(tenant, Encoding.UTF8.GetBytes(material));

    /// <summary>
    /// True when the request already holds this version: If-None-Match matches the ETag (weak
    /// comparison, <c>*</c> matches anything), or, with no If-None-Match at all, If-Modified-Since is
    /// at or after Last-Modified. RFC 9110 section 13.2.2 order.
    /// </summary>
    public static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset? lastModified)
    {
        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            if (!EntityTagHeaderValue.TryParse(etag, out var current)
                || !EntityTagHeaderValue.TryParseList(ifNoneMatch, out var presented))
                return false;

            return presented.Any(p => p.Tag == "*" || p.Compare(current, useStrongComparison: false));
        }

        return lastModified is { } at
               && HeaderUtilities.TryParseDate(request.Headers.IfModifiedSince.ToString(), out var since)
               && Seconds(at) <= since;
    }

    /// <summary>The Last-Modified the response already carries, if any.</summary>
    public static DateTimeOffset? LastModifiedOf(HttpResponse response) =>
        HeaderUtilities.TryParseDate(response.Headers.LastModified.ToString(), out var at) ? at : null;

    public static string TenantOf(HttpContext http) =>
        http.RequestServices?.GetService<TenantContext>()?.Slug ?? barakoCMS.Models.Tenant.DefaultSlug;

    // Anything outside a small set is percent-encoded from its UTF-8 bytes, '%' and ':' included, so
    // a tag is one header token, a type name cannot pose as another tag's parts, and two different
    // inputs never give the same tag.
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static DateTimeOffset Seconds(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
