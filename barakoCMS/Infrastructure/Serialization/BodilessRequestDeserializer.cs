using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace barakoCMS.Infrastructure.Serialization;

/// <summary>
/// The FastEndpoints request deserializer, reading JSON the way the default one does, except that a
/// GET, HEAD, DELETE or OPTIONS request with no body binds as if it declared no JSON content type.
/// </summary>
/// <remarks>
/// FastEndpoints reads a JSON body whenever the Content-Type says JSON, so a client that sets
/// <c>Content-Type: application/json</c> on every call had its GETs answered 400 by the serializer
/// failing on zero bytes (issue #681). Returning null makes the binder start from an empty request
/// and carry on with route, query and claim binding, which is exactly the path taken without the
/// header. POST, PUT and PATCH are left alone: they are expected to carry a body, so an empty one is
/// still reported.
/// </remarks>
internal static class BodilessRequestDeserializer
{
    public static Func<HttpRequest, Type, JsonSerializerContext?, CancellationToken, ValueTask<object?>> Create(
        JsonSerializerOptions options) =>
        (request, type, context, ct) => HasNoBody(request)
            ? ValueTask.FromResult<object?>(null)
            : request.ReadFromJsonAsync(type, context?.Options ?? options, ct);

    internal static bool HasNoBody(HttpRequest request) =>
        IsBodilessVerb(request.Method) &&
        (request.ContentLength == 0 ||
         request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false);

    static bool IsBodilessVerb(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
        HttpMethods.IsDelete(method) || HttpMethods.IsOptions(method);
}
