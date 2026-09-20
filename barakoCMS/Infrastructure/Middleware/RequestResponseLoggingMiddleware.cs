using System.Diagnostics;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Logging;

namespace barakoCMS.Infrastructure.Middleware;

public class RequestResponseLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestResponseLoggingMiddleware> logger,
    IMetricsService metrics)
{
    public async Task Invoke(HttpContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await next(context);
            sw.Stop();

            var statusCode = context.Response.StatusCode;
            metrics.TrackRequest(statusCode, sw.Elapsed.TotalMilliseconds);

            var level = statusCode >= 500 ? LogLevel.Error : statusCode >= 400 ? LogLevel.Warning : LogLevel.Information;

            logger.Log(level, "HTTP {Method} {Path} responded {StatusCode} in {Elapsed:0.0000}ms",
                LogSafe.Value(context.Request.Method),
                LogSafe.Value(context.Request.Path),
                statusCode,
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            metrics.TrackRequest(500, sw.Elapsed.TotalMilliseconds);
            logger.LogError(ex, "HTTP {Method} {Path} failed in {Elapsed:0.0000}ms",
                LogSafe.Value(context.Request.Method),
                LogSafe.Value(context.Request.Path),
                sw.Elapsed.TotalMilliseconds);

            throw; // Re-throw so upstream error handlers catch it
        }
    }
}
