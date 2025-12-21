using System.Diagnostics;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Middleware to monitor and log slow API requests.
/// Logs a warning when requests exceed the configured threshold.
/// </summary>
public class PerformanceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PerformanceMiddleware> _logger;
    private const int SlowRequestThresholdMs = 200;

    public PerformanceMiddleware(RequestDelegate next, ILogger<PerformanceMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();

            if (sw.ElapsedMilliseconds > SlowRequestThresholdMs)
            {
                _logger.LogWarning(
                    "Slow request: {Method} {Path} took {ElapsedMs}ms (Status: {StatusCode})",
                    context.Request.Method,
                    context.Request.Path,
                    sw.ElapsedMilliseconds,
                    context.Response.StatusCode);
            }
            else
            {
                _logger.LogDebug(
                    "Request: {Method} {Path} took {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path,
                    sw.ElapsedMilliseconds);
            }
        }
    }
}
