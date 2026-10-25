using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Pwa;

/// <summary>
/// Deletes device rows not seen for <c>Modules:Pwa:RetentionDays</c> days (180 by default, 0 keeps
/// them forever). A device that reports again after its row is gone simply starts a new one.
/// </summary>
internal sealed class PwaRetentionService(
    IDocumentStore store,
    PwaOptions options,
    ILogger<PwaRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error during the PWA device retention sweep");
                }

                await Task.Delay(SweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One sweep. Internal so a test can run it without waiting on the timer.</summary>
    internal async Task SweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (options.RetentionDays <= 0) return;

        var cutoff = nowUtc.AddDays(-options.RetentionDays);
        await using var session = store.LightweightSession();
        session.DeleteWhere<PwaInstall>(p => p.LastSeenAt < cutoff);
        await session.SaveChangesAsync(ct);
        logger.LogDebug("PWA device retention swept rows last seen before {Cutoff}", cutoff);
    }
}

/// <summary>Bound from <c>Modules:Pwa</c>.</summary>
public sealed class PwaOptions
{
    public const int DefaultRetentionDays = 180;

    /// <summary>Days a device may go unseen before its row is deleted. Zero keeps every row.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;
}
