using Microsoft.AspNetCore.HttpsPolicy;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Builds the <c>Strict-Transport-Security</c> policy from the <c>Hsts</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// HSTS is the one security header that cannot be taken back. A browser that has seen it refuses
/// plain HTTP to that host for the whole max-age, whatever the site sends afterwards, so a wrong
/// value is not a redeploy away from fixed. That is why the defaults here are the cautious ones and
/// the aggressive settings are opt-in.
/// </para>
/// <para>
/// 90 days is long enough to cover a returning visitor and short enough that a mistake ages out
/// within a quarter rather than a year. <c>includeSubDomains</c> is off: it applies to every
/// subdomain of the host, including any that is not on HTTPS yet and any that does not exist yet,
/// and an operator has to know their own DNS before turning it on. <c>preload</c> is not offered at
/// all, because getting onto the browsers' preload list takes a form and getting off it takes
/// months. See issue #130.
/// </para>
/// <para>
/// The header only ships outside Development, where <c>UseHsts</c> is in the pipeline, and the
/// framework's own excluded-host list keeps it off localhost so a developer's browser is never
/// pinned by a local run.
/// </para>
/// </remarks>
public static class HstsPolicy
{
    public const string Section = "Hsts";

    public const int DefaultMaxAgeDays = 90;

    public static void Configure(HstsOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);

        var days = section.GetValue<int?>("MaxAgeDays") ?? DefaultMaxAgeDays;
        if (days <= 0)
        {
            // max-age=0 is the instruction to forget the policy. Silently emitting it would look
            // exactly like HSTS being switched on while it switched HSTS off.
            throw new InvalidOperationException(
                $"{Section}:MaxAgeDays is {days}. A max-age of zero or less tells browsers to drop the "
              + "policy, so it disables HSTS rather than tightening it. Remove the setting to take the "
              + $"{DefaultMaxAgeDays} day default, or give it a positive number of days.");
        }

        options.MaxAge = TimeSpan.FromDays(days);
        options.IncludeSubDomains = section.GetValue<bool?>("IncludeSubDomains") ?? false;
        options.Preload = false;
    }
}
