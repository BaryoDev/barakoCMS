namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Refuses a read-only API key's writes before the endpoint binds the body. Without it a form or
/// multipart route answers 415 first, so the refusal would depend on what the caller sent.
/// </summary>
public sealed class ReadOnlyKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (await ApiKeyScopeProcessor.RefuseReadOnlyAsync(context, context.RequestAborted))
            return;
        await next(context);
    }
}
