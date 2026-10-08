using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Makes the body of a keyed write rewindable, so <see cref="IdempotencyFilter"/> can hash it after
/// FastEndpoints has already read it for binding.
/// </summary>
/// <remarks>
/// Only buffering is switched on here, not the hash. The hash waits for the pre-processor, which runs
/// after authentication, so an unauthenticated body is never read for this. The body is still bounded
/// by <c>RequestLimits:MaxBodyBytes</c>, and the buffer spills to disk past a small size.
/// </remarks>
internal sealed class IdempotencyRequestBuffering(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (IdempotencyFilter.IsKeyedWrite(context.Request))
        {
            context.Request.EnableBuffering();
        }

        return next(context);
    }
}
