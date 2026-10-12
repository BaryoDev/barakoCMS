namespace barakoCMS.Infrastructure.Services;

public interface IMetricsService
{
    void TrackRequest(int statusCode, double elapsedMilliseconds);

    /// <summary>
    /// The same, saying whether the request was a read a cache could answer (a GET or HEAD for
    /// content, not a probe) and whether a cache did.
    /// </summary>
    void TrackRequest(int statusCode, double elapsedMilliseconds, bool cacheableRead, bool cacheHit) =>
        TrackRequest(statusCode, elapsedMilliseconds);

    MetricsSummary GetSummary();
}

public class MetricsSummary
{
    public long TotalRequests { get; set; }
    public long TotalErrors { get; set; }
    public double AverageResponseTime { get; set; }
    public double ErrorRate { get; set; }

    /// <summary>When this process started counting, which is when the API started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whole seconds since <see cref="StartedAt"/>.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>The same numbers over the last 24 hours, plus the ones only a window can give.</summary>
    public MetricsWindow Last24Hours { get; set; } = new();
}

/// <summary>
/// Requests over a trailing window, kept per minute. The totals above are since the process started,
/// which on a host that has run for a month says nothing about this morning.
/// </summary>
public class MetricsWindow
{
    public long Requests { get; set; }
    public long Errors { get; set; }
    public double ErrorRate { get; set; }
    public double AverageResponseTime { get; set; }

    /// <summary>
    /// The 95th percentile response time in milliseconds, as the upper bound of the histogram bucket
    /// it falls in (see <see cref="MetricsService.LatencyBoundsMs"/>), so it can overstate by up to
    /// one bucket. Past the last bound it reads as that bound, so above 10 seconds it understates.
    /// Zero with no requests.
    /// </summary>
    public double P95ResponseTime { get; set; }

    /// <summary>
    /// The share of successful GET and HEAD requests answered from a cache: a <c>304 Not Modified</c>
    /// to a client holding the current ETag, or a response the output cache served without running
    /// the endpoint. Health probes and the Prometheus scrape are not counted. Zero with no such reads.
    /// </summary>
    public double CacheHitRate { get; set; }

    /// <summary>How many reads <see cref="CacheHitRate"/> counts as answered from a cache.</summary>
    public long CacheHits { get; set; }
}

public class MetricsService : IMetricsService
{
    /// <summary>The upper bounds of the latency histogram's buckets, in milliseconds.</summary>
    /// <remarks>
    /// Fixed buckets rather than every sample, so a minute costs the same memory at ten requests or
    /// ten thousand. Anything slower than the last bound lands in an overflow bucket reported as
    /// that bound.
    /// </remarks>
    public static readonly double[] LatencyBoundsMs =
        [1, 2, 5, 10, 15, 20, 30, 40, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000];

    private const int WindowMinutes = 24 * 60;

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly Minute[] _minutes = new Minute[WindowMinutes];

    private long _totalRequests = 0;
    private long _totalErrors = 0;
    private double _totalElapsed = 0;
    private readonly object _lock = new object();

    public MetricsService() : this(TimeProvider.System)
    {
    }

    public MetricsService(TimeProvider time)
    {
        _time = time;
        _startedAt = time.GetUtcNow();
        for (var i = 0; i < _minutes.Length; i++)
        {
            _minutes[i] = new Minute();
        }
    }

    public void TrackRequest(int statusCode, double elapsedMilliseconds) =>
        TrackRequest(statusCode, elapsedMilliseconds, cacheableRead: false, cacheHit: false);

    public void TrackRequest(int statusCode, double elapsedMilliseconds, bool cacheableRead, bool cacheHit)
    {
        var stamp = MinuteStamp(_time.GetUtcNow());

        lock (_lock)
        {
            _totalRequests++;
            if (statusCode >= 400)
            {
                _totalErrors++;
            }
            _totalElapsed += elapsedMilliseconds;

            var minute = _minutes[(int)(stamp % WindowMinutes)];
            if (minute.Stamp != stamp)
            {
                minute.Reset(stamp);
            }

            minute.Requests++;
            minute.Elapsed += elapsedMilliseconds;
            if (statusCode >= 400)
            {
                minute.Errors++;
            }
            var answered = statusCode is (>= 200 and < 300) or 304;
            if (cacheableRead && answered)
            {
                minute.Reads++;
                if (statusCode == 304 || cacheHit)
                {
                    minute.CacheHits++;
                }
            }
            minute.Latency[Bucket(elapsedMilliseconds)]++;
        }
    }

    public MetricsSummary GetSummary()
    {
        var now = _time.GetUtcNow();
        var newest = MinuteStamp(now);
        var oldest = newest - WindowMinutes + 1;

        lock (_lock)
        {
            var window = new MetricsWindow();
            var reads = 0L;
            var hits = 0L;
            var elapsed = 0.0;
            var latency = new long[LatencyBoundsMs.Length + 1];

            foreach (var minute in _minutes)
            {
                // A minute stamped after now is from before the clock was set back.
                if (minute.Stamp < oldest || minute.Stamp > newest)
                {
                    continue;
                }

                window.Requests += minute.Requests;
                window.Errors += minute.Errors;
                elapsed += minute.Elapsed;
                reads += minute.Reads;
                hits += minute.CacheHits;
                for (var i = 0; i < latency.Length; i++)
                {
                    latency[i] += minute.Latency[i];
                }
            }

            if (window.Requests > 0)
            {
                window.ErrorRate = (double)window.Errors / window.Requests;
                window.AverageResponseTime = elapsed / window.Requests;
                window.P95ResponseTime = Percentile(latency, window.Requests, 0.95);
            }

            window.CacheHits = hits;
            window.CacheHitRate = reads > 0 ? (double)hits / reads : 0;

            return new MetricsSummary
            {
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors,
                AverageResponseTime = _totalRequests > 0 ? _totalElapsed / _totalRequests : 0,
                ErrorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0,
                StartedAt = _startedAt,
                UptimeSeconds = (long)Math.Max(0, (now - _startedAt).TotalSeconds),
                Last24Hours = window,
            };
        }
    }

    private static long MinuteStamp(DateTimeOffset at) => at.ToUnixTimeSeconds() / 60;

    private static int Bucket(double elapsedMilliseconds)
    {
        for (var i = 0; i < LatencyBoundsMs.Length; i++)
        {
            if (elapsedMilliseconds <= LatencyBoundsMs[i])
            {
                return i;
            }
        }

        return LatencyBoundsMs.Length;
    }

    private static double Percentile(long[] latency, long count, double fraction)
    {
        var rank = (long)Math.Ceiling(count * fraction);
        var seen = 0L;
        for (var i = 0; i < latency.Length; i++)
        {
            seen += latency[i];
            if (seen >= rank)
            {
                return LatencyBoundsMs[Math.Min(i, LatencyBoundsMs.Length - 1)];
            }
        }

        return LatencyBoundsMs[^1];
    }

    private sealed class Minute
    {
        public long Stamp = long.MinValue;
        public long Requests;
        public long Errors;
        public long Reads;
        public long CacheHits;
        public double Elapsed;
        public readonly long[] Latency = new long[LatencyBoundsMs.Length + 1];

        public void Reset(long stamp)
        {
            Stamp = stamp;
            Requests = Errors = Reads = CacheHits = 0;
            Elapsed = 0;
            Array.Clear(Latency);
        }
    }
}
