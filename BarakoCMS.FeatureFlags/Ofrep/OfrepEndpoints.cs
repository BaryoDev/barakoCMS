using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.FeatureFlags.Ofrep;

/// <summary>
/// The OpenFeature Remote Evaluation Protocol (OFREP) over this module's flags (#628), so an
/// OpenFeature SDK with the OFREP provider reads them with no barakoCMS-specific client. The routes
/// sit at the protocol's own path from the origin, <c>/ofrep/v1/evaluate/flags</c>, because OFREP
/// providers append that path to the host and drop any path in the base URL.
/// </summary>
/// <remarks>
/// Covered: boolean flags, single and bulk evaluation, <c>FLAG_NOT_FOUND</c>, and an ETag on bulk
/// evaluation. The evaluation context is read for <c>targetingKey</c> only, and only from an
/// anonymous caller, where it picks the percentage-rollout bucket. Tenant and user always come
/// from the request itself: a context naming another tenant or user changes nothing, so it cannot
/// be used to read someone else's flags.
/// </remarks>
internal static class OfrepEvaluation
{
    public const string BasePath = "/ofrep/v1/evaluate/flags";

    public static FlagContext ContextFor(ClaimsPrincipal user, string tenantSlug, OfrepRequest? request, out FlagAudience audience)
    {
        var email = user.FindFirst("Username")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value;
        var signedIn = user.Identity?.IsAuthenticated == true;
        audience = signedIn ? FlagAudience.Authenticated : FlagAudience.Public;
        var bucket = email ?? (signedIn ? null : request?.TargetingKey) ?? tenantSlug;
        return new FlagContext(tenantSlug, email, bucket);
    }

    public static OfrepFlag Evaluation(FeatureFlag flag, FlagContext ctx)
    {
        var value = FeatureFlagService.Evaluate(flag, ctx);
        var reason = !flag.Enabled ? "DISABLED"
            : flag.TenantSlugs.Count > 0 || flag.UserEmails.Count > 0 ? "TARGETING_MATCH"
            : flag.RolloutPercent < 100 ? "SPLIT"
            : "STATIC";
        return new OfrepFlag
        {
            Key = flag.Key,
            Value = value,
            Reason = reason,
            Variant = value ? "on" : "off",
        };
    }
}

internal sealed class OfrepRequest
{
    public Dictionary<string, JsonElement>? Context { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? TargetingKey =>
        Context is not null && Context.TryGetValue("targetingKey", out var key) && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;
}

internal sealed class OfrepKeyRequest
{
    public string Key { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? Context { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public OfrepRequest Body => new() { Context = Context };
}

internal sealed class OfrepFlag
{
    public string Key { get; set; } = string.Empty;
    public bool Value { get; set; }
    public string Reason { get; set; } = "STATIC";
    public string Variant { get; set; } = "off";
    public Dictionary<string, object> Metadata { get; set; } = new();
}

internal sealed class OfrepError
{
    public string Key { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = "FLAG_NOT_FOUND";
    public string? ErrorDetails { get; set; }
}

internal sealed class OfrepBulkResponse
{
    public List<OfrepFlag> Flags { get; set; } = [];
}

/// <summary>POST /ofrep/v1/evaluate/flags/{key}: one flag, evaluated for the caller.</summary>
internal sealed class OfrepEvaluateFlagEndpoint(
    FeatureFlagService flags,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<OfrepKeyRequest>
{
    public override void Configure()
    {
        Post(OfrepEvaluation.BasePath + "/{key}");
        AllowAnonymous();
        Options(x => x.WithTags("FeatureFlags"));
    }

    public override async Task HandleAsync(OfrepKeyRequest req, CancellationToken ct)
    {
        var ctx = OfrepEvaluation.ContextFor(User, tenant.Slug, req.Body, out var audience);
        var flag = await flags.FindAsync(req.Key, ct);

        // A private flag answers exactly like a missing one to a caller who may not see it: the key
        // is the leak, the same rule as GET /api/feature-flags.
        if (flag is null || (audience == FlagAudience.Public && !flag.IsPublic))
        {
            await Send.ResponseAsync(new OfrepError
            {
                Key = req.Key,
                ErrorCode = "FLAG_NOT_FOUND",
                ErrorDetails = "No flag with this key is visible to the caller.",
            }, StatusCodes.Status404NotFound, ct);
            return;
        }

        await Send.ResponseAsync(OfrepEvaluation.Evaluation(flag, ctx), cancellation: ct);
    }
}

/// <summary>
/// POST /ofrep/v1/evaluate/flags: every flag the caller may see, evaluated. Carries
/// an ETag, and answers 304 to a matching If-None-Match, so a client-side provider can poll cheaply.
/// </summary>
internal sealed class OfrepEvaluateAllEndpoint(
    FeatureFlagService flags,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<OfrepRequest, OfrepBulkResponse>
{
    public override void Configure()
    {
        Post(OfrepEvaluation.BasePath);
        AllowAnonymous();
        Options(x => x.WithTags("FeatureFlags"));
    }

    public override async Task HandleAsync(OfrepRequest req, CancellationToken ct)
    {
        var ctx = OfrepEvaluation.ContextFor(User, tenant.Slug, req, out var audience);
        var visible = await flags.VisibleAsync(audience, ct);
        var response = new OfrepBulkResponse
        {
            Flags = visible.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => OfrepEvaluation.Evaluation(f, ctx)).ToList(),
        };

        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', response.Flags.Select(f => $"{f.Key}={f.Value}:{f.Reason}")))))[..32].ToLowerInvariant() + "\"";
        HttpContext.Response.Headers.ETag = etag;
        if (HttpContext.Request.Headers.IfNoneMatch.Any(v => v == etag))
        {
            await Send.StatusCodeAsync(StatusCodes.Status304NotModified, ct);
            return;
        }

        await Send.ResponseAsync(response, cancellation: ct);
    }
}
