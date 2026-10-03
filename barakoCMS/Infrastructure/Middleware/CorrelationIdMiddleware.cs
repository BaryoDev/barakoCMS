using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Gives every request one correlation id: on the response, on its log lines, on its span and on
/// what it stores.
/// </summary>
/// <remarks>
/// The id is the caller's <c>X-Correlation-ID</c> when that is a valid one, or else the trace id of
/// the request, so a caller that sent only <c>traceparent</c> finds its own trace id here. With
/// neither it is a fresh one.
///
/// The header is the caller's text, and it reaches the log, the response and the database. It is
/// checked first, and one that fails is replaced and never echoed.
/// </remarks>
public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeaderName = "X-Correlation-ID";

    public async Task Invoke(HttpContext context)
    {
        var span = Activity.Current;
        var correlationId = Correlation.Normalise(context.Request.Headers[CorrelationIdHeaderName].ToString())
            ?? Correlation.TraceIdOf(span)
            ?? Correlation.NewId();

        span?.SetTag(BarakoTracing.CorrelationIdTag, correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append(CorrelationIdHeaderName, new[] { correlationId });
            return Task.CompletedTask;
        });

        using (Correlation.Begin(correlationId))
        {
            await next(context);
        }
    }
}
