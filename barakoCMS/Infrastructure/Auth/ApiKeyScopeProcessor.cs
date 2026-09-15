using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Enforces API-key scope. Runs for every request but only acts when the caller authenticated with an
/// API key (<c>auth_method=apikey</c>). API keys are confined to the content surface (content,
/// content types, schemas) and to read vs write by HTTP method, except that erasing and rolling back
/// an entry need <c>content:destructive</c>. Everything else (managing users,
/// roles, tenants, or keys themselves) stays behind human JWTs, so a leaked key can never escalate
/// into platform administration. Human JWT callers are untouched: they carry no scope claims and use
/// the role gate + permission resolver as before.
/// </summary>
public sealed class ApiKeyScopeProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true) return;
        if (!user.HasClaim("auth_method", "apikey")) return;

        var path = http.Request.Path.Value ?? string.Empty;
        var isWrite = HttpMethods.IsPost(http.Request.Method)
                      || HttpMethods.IsPut(http.Request.Method)
                      || HttpMethods.IsPatch(http.Request.Method)
                      || HttpMethods.IsDelete(http.Request.Method);

        var required = RequiredScope(path, isWrite);
        if (required is null)
        {
            await Deny(http, "API keys are limited to the content API.", ct);
            return;
        }

        var scopes = user.FindAll("scope").Select(c => c.Value);
        if (!ApiKeyScopes.Satisfies(scopes, required))
            await Deny(http, $"This API key is missing the '{required}' scope.", ct);
    }

    // The scope a request needs, or null if API keys may not touch this path at all.
    private static string? RequiredScope(string path, bool isWrite)
    {
        if (Match(path, "/api/contents"))
        {
            if (IsDestructive(path))
                return ApiKeyScopes.ContentDestructive;
            return isWrite ? ApiKeyScopes.ContentWrite : ApiKeyScopes.ContentRead;
        }
        if (Match(path, "/api/content-types") || Match(path, "/api/schemas"))
            return isWrite ? ApiKeyScopes.ContentTypeWrite : ApiKeyScopes.ContentTypeRead;
        return null;
    }

    // DELETE /api/contents/{id}/erase and POST /api/contents/{id}/rollback/{versionId}, matched by
    // shape whatever the method, so a trailing slash or different casing that routing still matches
    // cannot reach them with a content:write key (#653). The segment count is exact, and the
    // rollback shape excludes by-slug: GET /api/contents/by-slug/{type}/{slug} has the same five
    // segments with a type name where the action sits, and "rollback" and "erase" are valid names.
    private static bool IsDestructive(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            4 => segments[3].Equals("erase", StringComparison.OrdinalIgnoreCase),
            5 => segments[3].Equals("rollback", StringComparison.OrdinalIgnoreCase)
                 && !segments[2].Equals("by-slug", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool Match(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static async Task Deny(HttpContext http, string message, CancellationToken ct)
    {
        http.Response.StatusCode = 403;
        // Writing the body short-circuits the endpoint (setting the status alone would not).
        await http.Response.WriteAsync(message, ct);
    }
}
