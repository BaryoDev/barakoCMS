using System.Diagnostics;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Infrastructure.Middleware;

public class RequestResponseLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestResponseLoggingMiddleware> _logger;
    private readonly IMetricsService _metrics;

    public RequestResponseLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestResponseLoggingMiddleware> logger,
        IMetricsService metrics)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task Invoke(HttpContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await _next(context);
            sw.Stop();

            var statusCode = context.Response.StatusCode;
            _metrics.TrackRequest(statusCode, sw.Elapsed.TotalMilliseconds);

            var level = statusCode >= 500 ? LogLevel.Error : statusCode >= 400 ? LogLevel.Warning : LogLevel.Information;

            _logger.Log(level, "HTTP {Method} {Path} responded {StatusCode} in {Elapsed:0.0000}ms",
                context.Request.Method,
                context.Request.Path,
                statusCode,
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.TrackRequest(500, sw.Elapsed.TotalMilliseconds);
            _logger.LogError(ex, "HTTP {Method} {Path} failed in {Elapsed:0.0000}ms",
                context.Request.Method,
                context.Request.Path,
                sw.Elapsed.TotalMilliseconds);

            throw; // Re-throw so upstream error handlers catch it
        }
    }
}
