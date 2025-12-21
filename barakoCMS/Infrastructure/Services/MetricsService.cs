using System.Collections.Concurrent;

namespace barakoCMS.Infrastructure.Services;

public interface IMetricsService
{
    void TrackRequest(int statusCode, double elapsedMilliseconds);
    MetricsSummary GetSummary();
}

public class MetricsSummary
{
    public long TotalRequests { get; set; }
    public long TotalErrors { get; set; }
    public double AverageResponseTime { get; set; }
    public double ErrorRate { get; set; }
}

public class MetricsService : IMetricsService
{
    private long _totalRequests = 0;
    private long _totalErrors = 0;
    private double _totalElapsed = 0;
    private readonly object _lock = new object();

    public void TrackRequest(int statusCode, double elapsedMilliseconds)
    {
        lock (_lock)
        {
            _totalRequests++;
            if (statusCode >= 400)
            {
                _totalErrors++;
            }
            _totalElapsed += elapsedMilliseconds;
        }
    }

    public MetricsSummary GetSummary()
    {
        lock (_lock)
        {
            return new MetricsSummary
            {
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors,
                AverageResponseTime = _totalRequests > 0 ? _totalElapsed / _totalRequests : 0,
                ErrorRate = _totalRequests > 0 ? (double)_totalErrors / _totalRequests : 0
            };
        }
    }
}
