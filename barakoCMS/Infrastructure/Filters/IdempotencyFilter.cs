using FastEndpoints;

namespace barakoCMS.Infrastructure.Filters;

public class IdempotencyFilter : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        if (context.HttpContext.Request.Method != "POST" && context.HttpContext.Request.Method != "PUT" && context.HttpContext.Request.Method != "PATCH")
        {
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey))
        {
            return;
        }
        Console.WriteLine($"[SERVER] Idempotency Key Found: {idempotencyKey}");

        var session = context.HttpContext.RequestServices.GetService<Marten.IDocumentSession>();
        if (session == null) return;

        var key = idempotencyKey.ToString();
        var existing = await session.LoadAsync<barakoCMS.Models.IdempotencyRecord>(key, ct);

        if (existing != null)
        {
            // Key already exists, conflict
            await context.HttpContext.Response.SendAsync("Request with this Idempotency-Key already processed.", 409, cancellation: ct);
            context.ValidationFailures.Add(new FluentValidation.Results.ValidationFailure("Idempotency-Key", "Duplicate Request"));
            return;
        }

        // Consuming the key
        session.Store(new barakoCMS.Models.IdempotencyRecord { Key = key });
        await session.SaveChangesAsync(ct);
    }
}
