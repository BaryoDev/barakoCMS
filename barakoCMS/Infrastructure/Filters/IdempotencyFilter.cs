using FastEndpoints;
using Microsoft.Extensions.Logging;

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

        var logger = context.HttpContext.RequestServices.GetService<ILogger<IdempotencyFilter>>();
        logger?.LogDebug("Idempotency Key Found: {IdempotencyKey}", idempotencyKey);

        var session = context.HttpContext.RequestServices.GetService<Marten.IDocumentSession>();
        if (session == null)
        {
            logger?.LogWarning("IDocumentSession not available for idempotency check");
            return;
        }

        var key = idempotencyKey.ToString();

        // Use atomic insert with unique constraint to prevent race conditions
        // The database unique index on IdempotencyRecord.Key will reject duplicates
        try
        {
            session.Store(new barakoCMS.Models.IdempotencyRecord { Key = key });
            await session.SaveChangesAsync(ct);
            logger?.LogDebug("Idempotency key stored: {IdempotencyKey}", key);
        }
        catch (Exception ex) when (
            ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("23505") || // PostgreSQL unique violation error code
            ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
            ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Key already exists - this is a duplicate request
            logger?.LogWarning("Duplicate idempotency key detected: {IdempotencyKey}", key);
            await context.HttpContext.Response.SendAsync("Request with this Idempotency-Key already processed.", 409, cancellation: ct);
            context.ValidationFailures.Add(new FluentValidation.Results.ValidationFailure("Idempotency-Key", "Duplicate Request"));
        }
    }
}
