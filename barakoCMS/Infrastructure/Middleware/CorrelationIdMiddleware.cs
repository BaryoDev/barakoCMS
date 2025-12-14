using Serilog.Context;

namespace barakoCMS.Infrastructure.Middleware;

public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private const string CorrelationIdHeaderName = "X-Correlation-ID";

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        string correlationId = GetCorrelationId(context);

        // Add the Correlation ID to the Response headers so the client knows it
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append(CorrelationIdHeaderName, new[] { correlationId });
            return Task.CompletedTask;
        });

        // Push the Correlation ID to the Serilog LogContext
        // This ensures the ID is attached to every log message generated during this request
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static string GetCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out var correlationId))
        {
            return correlationId;
        }

        return Guid.NewGuid().ToString();
    }
}
