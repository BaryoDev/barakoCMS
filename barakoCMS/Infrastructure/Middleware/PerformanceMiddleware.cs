using System.Diagnostics;
using barakoCMS.Infrastructure.Logging;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Middleware to monitor and log slow API requests.
/// Logs a warning when requests exceed the configured threshold.
/// </summary>
public class PerformanceMiddleware(RequestDelegate next, ILogger<PerformanceMiddleware> logger)
{
    private const int SlowRequestThresholdMs = 200;

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();

            if (sw.ElapsedMilliseconds > SlowRequestThresholdMs)
            {
                logger.LogWarning(
                    "Slow request: {Method} {Path} took {ElapsedMs}ms (Status: {StatusCode})",
                    LogSafe.Value(context.Request.Method),
                    LogSafe.Value(context.Request.Path),
                    sw.ElapsedMilliseconds,
                    context.Response.StatusCode);
            }
            else
            {
                logger.LogDebug(
                    "Request: {Method} {Path} took {ElapsedMs}ms",
                    LogSafe.Value(context.Request.Method),
                    LogSafe.Value(context.Request.Path),
                    sw.ElapsedMilliseconds);
            }
        }
    }
}
