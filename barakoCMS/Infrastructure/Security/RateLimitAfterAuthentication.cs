using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// The limits that need a verified caller: the quota per API key, and the named policies
/// partitioned by user or by API key.
/// </summary>
/// <remarks>
/// <para>
/// The rate limiter runs before authentication, so that it also bounds the lookups authentication
/// makes. All it can see there is what the request presents, and a caller can present a different
/// key on every request, so a bucket keyed on that is no quota. This runs after authentication and
/// counts against the id the handler verified. A partition exists only for a key or a user that
/// is stored.
/// </para>
/// <para>
/// A named policy met by a caller with no such id counts in one bucket shared by every such
/// caller, never in none. The API key quota applies only to a request an API key authenticated;
/// any other request stays on the limits it already had.
/// </para>
/// <para>
/// It also counts the built-in policies partitioned by user, which exist whatever is configured:
/// the email template preview (<see cref="RateLimitSetup.EmailPreviewPolicy"/>).
/// </para>
/// </remarks>
internal sealed class RateLimitAfterAuthentication
{
    public const string ApiKeyClaim = "apikey_id";
    public const string UserClaim = "UserId";

    private readonly RequestDelegate _next;
    private readonly RateLimitWindow? _apiKeyQuota;
    private readonly Dictionary<string, NamedRateLimit> _policies;
    private readonly PartitionedRateLimiter<Bucket> _limiter;

    public RateLimitAfterAuthentication(RequestDelegate next, IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        _next = next;

        var settings = RateLimitSetup.Read(configuration);
        _apiKeyQuota = settings.ApiKey;
        _policies = settings.Policies
            .Where(policy => policy.PartitionBy != RateLimitPartitionBy.Ip)
            .ToDictionary(policy => policy.Name, StringComparer.Ordinal);

        // A configured policy cannot take a built-in name, so this replaces nothing.
        _policies[RateLimitSetup.EmailPreviewPolicy] = new NamedRateLimit(
            RateLimitSetup.EmailPreviewPolicy, RateLimitSetup.EmailPreview, RateLimitPartitionBy.User);

        var limiter = PartitionedRateLimiter.Create<Bucket, string>(bucket =>
            RateLimitPartition.GetFixedWindowLimiter(bucket.Key, _ => RateLimitSetup.Options(bucket.Window)));
        _limiter = limiter;

        // Stopped, not Stopping: a request still in flight while the host drains must not meet a
        // disposed limiter.
        lifetime.ApplicationStopped.Register(limiter.Dispose);
    }

    public Task InvokeAsync(HttpContext context) => Limited(context, _limiter);

    private async Task Limited(HttpContext context, PartitionedRateLimiter<Bucket> limiter)
    {
        if (_apiKeyQuota is { } quota
            && VerifiedClaim(context.User, ApiKeyClaim) is { } keyId
            && !await Acquire(limiter, context, new Bucket($"apikey|{keyId}", quota)))
        {
            return;
        }

        var named = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        if (named is not null
            && _policies.TryGetValue(named, out var policy)
            && !await Acquire(limiter, context, new Bucket(PolicyKey(policy, context.User), policy.Window)))
        {
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// The bucket a named policy counts this caller in. A caller without the id the policy
    /// partitions by shares one bucket with every other such caller.
    /// </summary>
    internal static string PolicyKey(NamedRateLimit policy, ClaimsPrincipal user)
    {
        var claim = policy.PartitionBy == RateLimitPartitionBy.ApiKey ? ApiKeyClaim : UserClaim;
        return VerifiedClaim(user, claim) is { } id
            ? $"policy|{policy.Name}|{claim}|{id}"
            : $"policy|{policy.Name}|shared";
    }

    private static string? VerifiedClaim(ClaimsPrincipal user, string type)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var value = user.FindFirst(type)?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static async Task<bool> Acquire(PartitionedRateLimiter<Bucket> limiter, HttpContext context, Bucket bucket)
    {
        using var lease = await limiter.AcquireAsync(bucket, 1, context.RequestAborted);
        if (lease.IsAcquired)
        {
            return true;
        }

        await RateLimitSetup.Reject(context, lease, context.RequestAborted);
        return false;
    }

    private readonly record struct Bucket(string Key, RateLimitWindow Window);
}
