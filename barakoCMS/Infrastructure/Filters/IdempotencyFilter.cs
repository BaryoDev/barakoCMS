using FastEndpoints;

namespace barakoCMS.Infrastructure.Filters;

public class IdempotencyFilter : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        Console.WriteLine($"[SERVER] IdempotencyFilter Running. Path: {context.HttpContext.Request.Path}");

        if (context.HttpContext.Request.Method != "POST" && context.HttpContext.Request.Method != "PUT" && context.HttpContext.Request.Method != "PATCH")
        {
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey))
        {
            return; // No idempotency key, proceed normally
        }
        Console.WriteLine($"[SERVER] Idempotency Key Found: {idempotencyKey}");

        // TODO: Implement actual idempotency check using a distributed cache or database
        // For now, we just acknowledge the key exists
        await Task.CompletedTask;
    }
}
