using System.Security.Cryptography;
using System.Text;
using Marten;

namespace BarakoCMS.FeatureFlags;

/// <summary>Reads and evaluates feature flags for a request context.</summary>
public class FeatureFlagService(IQuerySession session)
{
    /// <summary>Is a flag on for this context? Returns <paramref name="fallback"/> if the flag doesn't exist.</summary>
    public async Task<bool> IsEnabledAsync(string key, FlagContext ctx, bool fallback = false, CancellationToken ct = default)
    {
        var flag = await session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);
        return flag is null ? fallback : Evaluate(flag, ctx);
    }

    /// <summary>The flag with this key, or null.</summary>
    internal Task<FeatureFlag?> FindAsync(string key, CancellationToken ct = default) =>
        session.Query<FeatureFlag>().FirstOrDefaultAsync(f => f.Key == key, ct);

    /// <summary>Every flag a caller of this audience may be told about.</summary>
    internal async Task<IReadOnlyList<FeatureFlag>> VisibleAsync(FlagAudience audience, CancellationToken ct = default)
    {
        var all = await session.Query<FeatureFlag>().ToListAsync(ct);
        return all.Where(f => Visible(f, audience)).ToList();
    }

    /// <summary>May a caller of this audience be told this flag exists?</summary>
    internal static bool Visible(FeatureFlag flag, FlagAudience audience) => audience switch
    {
        FlagAudience.Admin => true,
        FlagAudience.Authenticated => flag.EffectiveVisibility() != FlagVisibility.Admin,
        _ => flag.EffectiveVisibility() == FlagVisibility.Public,
    };

    /// <summary>Anonymous is Public, signed in is Authenticated, and a flag manager is Admin.</summary>
    internal static async Task<FlagAudience> AudienceAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal user,
        Microsoft.Extensions.Configuration.IConfiguration configuration, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true) return FlagAudience.Public;
        return await FlagAdmin.AllowedAsync(session, user, configuration, ct)
            ? FlagAudience.Admin
            : FlagAudience.Authenticated;
    }

    /// <summary>
    /// Every flag's on/off result for this context, for the UI to gate on. The audience decides
    /// which keys are disclosed at all, which is a separate question from what each one evaluates
    /// to: targeting already makes a private flag evaluate false for a stranger, and returning it
    /// as false still hands them the name.
    /// </summary>
    public async Task<Dictionary<string, bool>> EvaluateAllAsync(FlagContext ctx, FlagAudience audience, CancellationToken ct = default)
    {
        var flags = await session.Query<FeatureFlag>().ToListAsync(ct);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in flags)
        {
            if (!Visible(f, audience)) continue;
            result[f.Key] = Evaluate(f, ctx);
        }
        return result;
    }

    /// <summary>
    /// The public subset. This is what an unstated audience gets, so a caller that has not thought
    /// about who is asking cannot leak a key by omission.
    /// </summary>
    public Task<Dictionary<string, bool>> EvaluateAllAsync(FlagContext ctx, CancellationToken ct = default) =>
        EvaluateAllAsync(ctx, FlagAudience.Public, ct);

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
