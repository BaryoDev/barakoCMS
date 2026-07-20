using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Analytics.Umami;

// Read-side DTOs the endpoints return. Deliberately flat and framework-free so the admin can bind
// them directly.
public sealed record UmamiWebsite(string Id, string Name, string Domain);

public sealed record UmamiStat(long Value, long Previous);

/// <summary>Headline counters for a window. Bounces/TotalTime power bounce-rate and avg-visit-time.</summary>
public sealed record UmamiSummary(
    UmamiStat Pageviews,
    UmamiStat Visitors,
    UmamiStat Visits,
    UmamiStat Bounces,
    UmamiStat TotalTime);

public sealed record UmamiSeriesPoint(string X, long Y);

public sealed record UmamiSeries(IReadOnlyList<UmamiSeriesPoint> Pageviews, IReadOnlyList<UmamiSeriesPoint> Sessions);

public sealed record UmamiMetric(string X, long Y);

/// <summary>Signals the caller (an endpoint) that Umami rejected us or is unreachable, so it can map
/// to a clean 502/503 instead of leaking a raw HttpRequestException.</summary>
public sealed class UmamiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IUmamiClient
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<UmamiWebsite>> GetWebsitesAsync(CancellationToken ct);
    Task<UmamiWebsite> CreateWebsiteAsync(string name, string domain, CancellationToken ct);
    Task<UmamiSummary> GetSummaryAsync(string websiteId, long startAt, long endAt, CancellationToken ct);
    Task<UmamiSeries> GetSeriesAsync(string websiteId, long startAt, long endAt, string unit, CancellationToken ct);
    Task<IReadOnlyList<UmamiMetric>> GetMetricsAsync(string websiteId, string type, long startAt, long endAt, int limit, CancellationToken ct);
    /// <summary>Visitors active in the last few minutes — the fastest signal that the tracker is live.</summary>
    Task<long> GetActiveAsync(string websiteId, CancellationToken ct);
    string TrackingSnippet(string websiteId);
}

/// <summary>
/// Thin server-side proxy over the Umami REST API. Authenticates with the configured account
/// (POST /api/auth/login), caches the bearer token across requests, and re-authenticates once on a
/// 401. Registered as a typed HttpClient; the token cache is shared statically since typed clients
/// are transient.
/// </summary>
public sealed class UmamiClient : IUmamiClient
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly UmamiOptions _options;
    private readonly ILogger<UmamiClient> _logger;

    public UmamiClient(HttpClient http, IOptions<UmamiOptions> options, ILogger<UmamiClient> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public bool IsConfigured => _options.IsConfigured;

    public string TrackingSnippet(string websiteId)
    {
        var host = (_options.PublicUrl ?? _options.BaseUrl).TrimEnd('/');
        return $"<script defer src=\"{host}/script.js\" data-website-id=\"{websiteId}\"></script>";
    }

    public async Task<IReadOnlyList<UmamiWebsite>> GetWebsitesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("api/websites?pageSize=200", ct);
        // Newer Umami wraps the list as { data: [...], count, page }; older returns a bare array.
        var array = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("data", out var data) ? data : default;
        var list = new List<UmamiWebsite>();
        if (array.ValueKind == JsonValueKind.Array)
            foreach (var w in array.EnumerateArray())
                list.Add(new UmamiWebsite(
                    Str(w, "id"), Str(w, "name"), Str(w, "domain")));
        return list;
    }

    public async Task<UmamiWebsite> CreateWebsiteAsync(string name, string domain, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Post, "api/websites",
            () => JsonContent.Create(new { name, domain }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new UmamiUnavailableException($"Umami rejected the new website ({(int)res.StatusCode}): {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        var w = doc.RootElement;
        return new UmamiWebsite(Str(w, "id"), Str(w, "name"), Str(w, "domain"));
    }

    public async Task<UmamiSummary> GetSummaryAsync(string websiteId, long startAt, long endAt, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"api/websites/{Uri.EscapeDataString(websiteId)}/stats?startAt={startAt}&endAt={endAt}", ct);
        var r = doc.RootElement;
        // Umami v3 returns bare counts plus a "comparison" object holding the previous period; v2
        // returned each metric as { value, prev }. Stat() handles both.
        var cmp = r.TryGetProperty("comparison", out var c) ? c : default;
        return new UmamiSummary(
            Stat(r, cmp, "pageviews"), Stat(r, cmp, "visitors"), Stat(r, cmp, "visits"),
            Stat(r, cmp, "bounces"), Stat(r, cmp, "totaltime"));
    }

    public async Task<UmamiSeries> GetSeriesAsync(string websiteId, long startAt, long endAt, string unit, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"api/websites/{Uri.EscapeDataString(websiteId)}/pageviews?startAt={startAt}&endAt={endAt}&unit={unit}&timezone=UTC", ct);
        var r = doc.RootElement;
        return new UmamiSeries(Points(r, "pageviews"), Points(r, "sessions"));
    }

    public async Task<IReadOnlyList<UmamiMetric>> GetMetricsAsync(string websiteId, string type, long startAt, long endAt, int limit, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"api/websites/{Uri.EscapeDataString(websiteId)}/metrics?type={Uri.EscapeDataString(type)}&startAt={startAt}&endAt={endAt}&limit={limit}", ct);
        var list = new List<UmamiMetric>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var m in doc.RootElement.EnumerateArray())
                list.Add(new UmamiMetric(Str(m, "x"), Num(m, "y")));
        return list;
    }

    public async Task<long> GetActiveAsync(string websiteId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"api/websites/{Uri.EscapeDataString(websiteId)}/active", ct);
        var r = doc.RootElement;
        // Umami versions differ: { visitors: N } (v3), [{ x: N }] (v2), or a bare number.
        if (r.ValueKind == JsonValueKind.Number) return r.GetInt64();
        if (r.ValueKind == JsonValueKind.Object) return Num(r, "visitors");
        if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0)
            return Num(r[0], "x");
        return 0;
    }

    // --- HTTP plumbing -----------------------------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new UmamiUnavailableException($"Umami request '{path}' failed ({(int)res.StatusCode}): {Trim(body)}");
        return JsonDocument.Parse(body);
    }

    // Sends with a cached bearer token, re-authenticating once if Umami answers 401.
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            throw new UmamiUnavailableException("Umami is not configured (set Umami:Enabled and Umami:BaseUrl).");

        var token = await GetTokenAsync(forceRefresh: false, ct);
        var res = await SendOnceAsync(method, path, content, token, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            res.Dispose();
            token = await GetTokenAsync(forceRefresh: true, ct);
            res = await SendOnceAsync(method, path, content, token, ct);
        }
        return res;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, Func<HttpContent>? content, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null) req.Content = content();
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UmamiUnavailableException($"Could not reach Umami at {_options.BaseUrl}.", ex);
        }
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _token;

        await TokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _token;

            HttpResponseMessage res;
            try
            {
                res = await _http.PostAsJsonAsync("api/auth/login",
                    new { username = _options.Username, password = _options.Password }, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new UmamiUnavailableException($"Could not reach Umami at {_options.BaseUrl}.", ex);
            }

            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new UmamiUnavailableException($"Umami login failed ({(int)res.StatusCode}). Check Umami:Username/Password.");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("token", out var t) || t.GetString() is not { } tok)
                throw new UmamiUnavailableException("Umami login response had no token.");

            _token = tok;
            // Umami tokens are long-lived; refresh well before any plausible expiry and on any 401.
            _tokenExpiry = DateTimeOffset.UtcNow.AddHours(6);
            return _token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    // --- JSON helpers (tolerant of shape differences across Umami versions) -------------------

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static long Num(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static UmamiStat Stat(JsonElement root, JsonElement comparison, string prop)
    {
        if (!root.TryGetProperty(prop, out var v)) return new UmamiStat(0, 0);
        // v2: { value, prev }. v3: a bare number, with the previous period under comparison[prop].
        if (v.ValueKind == JsonValueKind.Object)
            return new UmamiStat(Num(v, "value"), Num(v, "prev"));
        long value = v.TryGetInt64(out var n) ? n : 0;
        long prev = comparison.ValueKind == JsonValueKind.Object ? Num(comparison, prop) : 0;
        return new UmamiStat(value, prev);
    }

    private static IReadOnlyList<UmamiSeriesPoint> Points(JsonElement root, string prop)
    {
        var list = new List<UmamiSeriesPoint>();
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
                list.Add(new UmamiSeriesPoint(Str(p, "x"), Num(p, "y")));
        return list;
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
