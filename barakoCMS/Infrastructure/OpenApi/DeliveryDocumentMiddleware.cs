using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.OpenApi;

/// <summary>
/// Merges the runtime content-type paths from <see cref="DeliveryDocument"/> into the generated
/// OpenAPI document on its way out.
/// </summary>
/// <remarks>
/// It has to happen here rather than in a document processor. NSwag builds the document once and
/// keeps it, and a content type is created by a user on a Tuesday afternoon, so a document built at
/// startup would never mention it. Rewriting the response is what makes "create a type, refresh
/// Swagger, see it" true with no restart.
///
/// Registered immediately before <c>UseSwaggerGen</c>, and only when Swagger is enabled at all, so
/// on a deployment with Swagger off this code never runs.
///
/// The document is per tenant because <see cref="ContentTypeDefinition"/> is, and the session this
/// resolves is already scoped to the tenant the resolution middleware picked.
/// </remarks>
internal sealed class DeliveryDocumentMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DeliveryDocumentCache _cache;
    private readonly ILogger<DeliveryDocumentMiddleware> _logger;

    public DeliveryDocumentMiddleware(
        RequestDelegate next,
        DeliveryDocumentCache cache,
        ILogger<DeliveryDocumentMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>The FastEndpoints/NSwag document route: /swagger/{documentName}/swagger.json.</summary>
    internal static string? DocumentName(PathString path)
    {
        if (!path.HasValue)
            return null;

        var segments = path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3
               && segments[0].Equals("swagger", StringComparison.OrdinalIgnoreCase)
               && segments[2].Equals("swagger.json", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var documentName = HttpMethods.IsGet(context.Request.Method)
            ? DocumentName(context.Request.Path)
            : null;

        if (documentName is null)
        {
            await _next(context);
            return;
        }

        var tenant = context.RequestServices.GetRequiredService<TenantContext>().Slug;

        var cached = _cache.Get(tenant, documentName);
        if (cached is not null)
        {
            await WriteAsync(context, cached);
            return;
        }

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        var generated = buffer.ToArray();

        // Anything that is not a successful JSON document is passed through untouched: an error
        // page, a 404 from a document name that does not exist, a 304.
        if (context.Response.StatusCode != StatusCodes.Status200OK
            || context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            context.Response.ContentLength = generated.Length;
            await original.WriteAsync(generated, context.RequestAborted);
            return;
        }

        try
        {
            var merged = await MergeAsync(context, generated);
            _cache.Set(tenant, documentName, merged);
            await WriteAsync(context, merged);
        }
        catch (Exception ex)
        {
            // The generated document is still correct without the content-type paths, and serving
            // it beats a 500 on the page a developer opens to find out what the API does. Not
            // cached, so a transient database failure costs one request rather than a minute.
            _logger.LogError(ex, "Could not add the delivery paths to the OpenAPI document; serving it unchanged");
            await WriteAsync(context, generated);
        }
    }

    private static async Task WriteAsync(HttpContext context, byte[] body)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static async Task<byte[]> MergeAsync(HttpContext context, byte[] generated)
    {
        var session = context.RequestServices.GetRequiredService<IQuerySession>();
        var types = await session.Query<ContentTypeDefinition>()
            .Where(t => t.IsPubliclyDeliverable)
            .ToListAsync(context.RequestAborted);

        if (types.Count == 0)
            return generated;

        var fragment = DeliveryDocument.Build(types);
        var document = JsonNode.Parse(generated)?.AsObject();
        if (document is null)
            return generated;

        var paths = document["paths"]?.AsObject();
        if (paths is null)
        {
            paths = new JsonObject();
            document["paths"] = paths;
        }

        foreach (var path in fragment["paths"]!.AsObject().ToList())
        {
            // A generated path never replaces a real one. /api/public/{type} and the module routes
            // under /api/public/ are registered endpoints, and losing one from the document would
            // be a worse defect than a content type going undocumented.
            if (paths.ContainsKey(path.Key))
                continue;

            paths[path.Key] = path.Value!.DeepClone();
        }

        var components = document["components"]?.AsObject();
        if (components is null)
        {
            components = new JsonObject();
            document["components"] = components;
        }

        var schemas = components["schemas"]?.AsObject();
        if (schemas is null)
        {
            schemas = new JsonObject();
            components["schemas"] = schemas;
        }

        foreach (var schema in fragment["schemas"]!.AsObject().ToList())
        {
            if (schemas.ContainsKey(schema.Key))
                continue;

            schemas[schema.Key] = schema.Value!.DeepClone();
        }

        return JsonSerializer.SerializeToUtf8Bytes(document);
    }
}
