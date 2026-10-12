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
            metrics.TrackRequest(statusCode, sw.Elapsed.TotalMilliseconds,
                CacheableRead(context), ServedFromOutputCache(context));

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

    /// <summary>
    /// Set on every request that reaches the endpoints, which sit inside the output cache. A cached
    /// response is written by the output cache without going further, so a request to a cached
    /// endpoint that never got the mark was answered from the cache.
    /// </summary>
    internal const string ReachedEndpointsKey = "barako:reached-endpoints";

    private static bool CacheableRead(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && !barakoCMS.Infrastructure.Health.HealthProbePaths.IsHealthPath(context.Request.Path.Value)
        && !context.Request.Path.StartsWithSegments("/metrics");

    private static bool ServedFromOutputCache(HttpContext context) =>
        !context.Items.ContainsKey(ReachedEndpointsKey)
        && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.OutputCaching.IOutputCachePolicy>() is not null;
}
