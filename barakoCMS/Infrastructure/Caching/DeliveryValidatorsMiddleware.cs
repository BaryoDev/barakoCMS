using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// Gives a delivery read's 200 a weak ETag over the body it sends, and answers 304 with no body when
/// the request already holds that version.
/// </summary>
/// <remarks>
/// Only endpoints carrying <see cref="DeliveryCache.Validators"/> are buffered, so a stream or a file
/// download is never held in memory here. The body is hashed after the endpoint has built it, so
/// anything that changes what a caller is sent, a resolved reference or a field made non-Public,
/// changes the tag too. A response that is not for a shared cache, a preview for one, gets no tag.
/// </remarks>
internal sealed class DeliveryValidatorsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.GetEndpoint()?.Metadata.GetMetadata<DeliveryValidatorsMetadata>() is null)
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        var response = context.Response;
        if (response.StatusCode == StatusCodes.Status200OK && DeliveryCache.IsShared(response))
        {
            var etag = response.Headers.ETag.Count > 0
                ? response.Headers.ETag.ToString()
                : DeliveryCache.WeakETag(DeliveryCache.TenantOf(context), buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            response.Headers.ETag = etag;

            if (DeliveryCache.IsNotModified(context.Request, etag, DeliveryCache.LastModifiedOf(response)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.Headers.Remove(HeaderNames.ContentType);
                return;
            }
        }

        if (buffer.Length > 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
    }
}
