using System.Security.Cryptography;
using System.Text;
using Marten;

namespace BarakoCMS.FeatureFlags;

/// <summary>Reads and evaluates feature flags for a request context.</summary>
public class FeatureFlagService
{
    private readonly IQuerySession _session;
    public FeatureFlagService(IQuerySession session) => _session = session;

    /// <summary>Is a flag on for this context? Returns <paramref name="fallback"/> if the flag doesn't exist.</summary>
    public async Task<bool> IsEnabledAsync(string key, FlagContext ctx, bool fallback = false, CancellationToken ct = default)
    {
        var flag = await _session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        return flag is null ? fallback : Evaluate(flag, ctx);
    }

    /// <summary>Every flag's on/off result for this context — for the UI to gate on.</summary>
    public async Task<Dictionary<string, bool>> EvaluateAllAsync(FlagContext ctx, CancellationToken ct = default)
    {
        var flags = await _session.Query<FeatureFlag>().ToListAsync(ct);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in flags) result[f.Key] = Evaluate(f, ctx);
        return result;
    }

    /// <summary>Pure evaluation: master toggle, then club, user, and percentage targeting.</summary>
    public static bool Evaluate(FeatureFlag flag, FlagContext ctx)
    {
        if (!flag.Enabled) return false;

        if (flag.TenantSlugs.Count > 0 &&
            (ctx.TenantSlug is null || !flag.TenantSlugs.Contains(ctx.TenantSlug, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (flag.UserEmails.Count > 0 &&
            (ctx.UserEmail is null || !flag.UserEmails.Contains(ctx.UserEmail, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (flag.RolloutPercent < 100)
        {
            var seed = ctx.BucketKey ?? ctx.UserEmail ?? ctx.TenantSlug ?? "";
            if (Bucket($"{flag.Key}:{seed}") >= flag.RolloutPercent) return false;
        }

        return true;
    }

    /// <summary>Stable 0..99 bucket for a string, so the same subject always lands the same way.</summary>
    private static int Bucket(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return (int)(BitConverter.ToUInt32(hash, 0) % 100);
    }
}
