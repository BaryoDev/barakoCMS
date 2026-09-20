using Serilog.Context;

namespace barakoCMS.Infrastructure.Middleware;

public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeaderName = "X-Correlation-ID";

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
            await next(context);
        }
    }

    private static string GetCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out var correlationId))
        {
            return correlationId.ToString() ?? Guid.NewGuid().ToString();
        }

        return Guid.NewGuid().ToString();
    }
}
