using Microsoft.Extensions.DependencyInjection.Extensions;

namespace barakoCMS.Infrastructure.Caching.Purge;

internal static class CdnPurgeRegistration
{
    /// <summary>The gate, signal, sender and sweep behind purge on commit (#561). The listener is added to the store.</summary>
    public static void AddCdnPurgeOnCommit(this IServiceCollection services)
    {
        services.TryAddSingleton<CdnPurgeGate>();
        services.TryAddSingleton<CdnPurgeSignal>();
        services.TryAddSingleton<CdnPurgeSender>();
        services.AddHostedService<CdnPurgeSweepService>();
    }
}
