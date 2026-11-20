using System.Diagnostics;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>What one sweep did.</summary>
/// <param name="Rows">How many pending rows were settled (sent, or dropped).</param>
/// <param name="Pending">True when some tenant still had rows at the start, so the caller looks again soon.</param>
internal readonly record struct CdnPurgeSweep(int Rows, bool Pending);

/// <summary>
/// Delivers the pending purges (#561): takes the rows past the debounce window, merges a tenant's
/// tags, calls the provider and deletes the rows on success.
/// </summary>
/// <remarks>
/// Work is claimed the way <see cref="BuildHookSweepService"/> claims builds. A row is taken with a
/// versioned write that stamps <see cref="PendingCdnPurge.ClaimedAt"/>, and a node that loses the
/// race skips the tenant, so two nodes never send the same rows. A claim older than five minutes is
/// taken again, which is how a crash between the claim and the call is recovered. A failed try is
/// retried with doubling backoff up to the attempt cap and then dropped. Each try writes one
/// <see cref="WebhookDelivery"/> row with the host (never a path), the status and the tag count. The
/// token and the tags are never recorded.
/// </remarks>
internal sealed class CdnPurgeSender(
    IDocumentStore store,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ISecretProtector protector,
    ILoggerFactory loggerFactory)
{
    public const string DeliveryEvent = "CdnPurge";
    public const string TagCountHeader = "X-Purge-Tag-Count";

    private const string PartitionsWithPendingPurgesSql = "select distinct tenant_id from public.mt_doc_pending_cdn_purges";
    private const int RowsPerFlush = 200;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<CdnPurgeSender>();

    public async Task<CdnPurgeSweep> SweepAllTenantsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var tenants = await TenantPartitions.ListAsync(store, configuration, PartitionsWithPendingPurgesSql, ct);
        var settled = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                settled += await SweepTenantAsync(tenantId, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sending the pending CDN purges for one tenant failed; the others go on");
            }
        }

        return new CdnPurgeSweep(settled, tenants.Count > 0);
    }

    /// <summary>Sends one tenant's due rows as one flush. A test drives it without the timer.</summary>
    public async Task<int> SweepTenantAsync(string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var options = CdnPurgeOptions.From(configuration);
        var tenant = TenantScopes.SlugFor(tenantId);
        var dueBefore = now - options.Debounce;
        var reclaimBefore = now - ClaimTimeout;

        await using var session = store.LightweightSession(tenantId);
        var due = await session.Query<PendingCdnPurge>()
            .Where(p => p.WrittenAt <= dueBefore
                && (p.RetryAt == null || p.RetryAt <= now)
                && (p.ClaimedAt == null || p.ClaimedAt < reclaimBefore))
            .OrderBy(p => p.WrittenAt)
            .Take(RowsPerFlush)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var row in due)
        {
            row.ClaimedAt = now;
            session.Store(row);
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ConcurrencyConflict.Is(ex))
        {
            // Another node took them first.
            return 0;
        }

        var tags = CdnPurgeBounds.Collapse(due.SelectMany(r => r.Tags), tenant, options.MaxTagsPerFlush, out var level);
        _logger.LogInformation(
            "CDN purge flush for tenant {Tenant}: {Rows} pending row(s), {Tags} tag(s), collapse {Level}",
            tenant, due.Count, tags.Count, level);

        await using var query = store.QuerySession();
        var resolver = new CdnPurgeResolver(query, protector, configuration, httpClientFactory, loggerFactory);
        if (await resolver.ResolveAsync(tenant, ct) is not var (target, purger))
        {
            _logger.LogInformation("Tenant {Tenant} turned CDN purging off; {Rows} pending row(s) dropped", tenant, due.Count);
            await DropAsync(session, due, ct);
            return due.Count;
        }

        var attempt = due.Max(r => r.Attempts) + 1;
        var (result, elapsed) = await CallAsync(purger, tags, options, ct);

        session.Store(NewDeliveryRow(target, result, tags.Count, attempt, elapsed, now));
        if (result.Succeeded || !result.Retryable || attempt >= options.MaxAttempts)
        {
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "CDN purge for tenant {Tenant} gave up after {Attempts} attempt(s), status {Status}; {Rows} pending row(s) dropped",
                    tenant, attempt, result.Status, due.Count);
            }

            await DropAsync(session, due, ct);
            return due.Count;
        }

        var wait = options.BackoffFor(attempt);
        if (result.RetryAfter is { } asked && asked > wait)
        {
            wait = asked < CdnPurgeOptions.MaxBackoff ? asked : CdnPurgeOptions.MaxBackoff;
        }

        foreach (var row in due)
        {
            row.Attempts = attempt;
            row.RetryAt = now + wait;
            row.ClaimedAt = null;
            session.Store(row);
        }

        await SaveQuietlyAsync(session, ct);
        return 0;
    }

    private static async Task<(CdnPurgeResult Result, TimeSpan Elapsed)> CallAsync(
        ICdnPurger purger, IReadOnlyList<string> tags, CdnPurgeOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        var clock = Stopwatch.StartNew();
        CdnPurgeResult result;
        try
        {
            result = await purger.PurgeTagsAsync(tags, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new CdnPurgeResult(false, null, "The provider did not answer within the timeout", Retryable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry a request detail.
            result = new CdnPurgeResult(false, null, $"Unexpected failure: {ex.GetType().Name}", Retryable: true);
        }

        return (result, clock.Elapsed);
    }

    private async Task DropAsync(IDocumentSession session, IReadOnlyList<PendingCdnPurge> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            session.Delete(row);
        }

        await SaveQuietlyAsync(session, ct);
    }

    private async Task SaveQuietlyAsync(IDocumentSession session, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ConcurrencyConflict.Is(ex))
        {
            _logger.LogWarning("Another node changed the pending CDN purge rows first; this node leaves them to it");
        }
    }

    private static WebhookDelivery NewDeliveryRow(
        CdnPurgeTarget target, CdnPurgeResult result, int tagCount, int attempt, TimeSpan elapsed, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = Guid.Empty,
        Url = WebhookAction.Redact(target.BaseUrl),
        Event = DeliveryEvent,
        RequestHeaders = new Dictionary<string, string> { [TagCountHeader] = tagCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        Attempt = attempt,
        ResponseStatus = result.Status,
        DurationMs = (long)elapsed.TotalMilliseconds,
        Error = result.Succeeded ? null : result.Error,
        CreatedAt = now,
    };
}

/// <summary>Runs <see cref="CdnPurgeSender"/> on an interval, and early when a commit on this node asks.</summary>
internal sealed class CdnPurgeSweepService(
    CdnPurgeSender sender,
    CdnPurgeSignal signal,
    IConfiguration configuration,
    ILogger<CdnPurgeSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = CdnPurgeOptions.From(configuration);
            var wait = options.IdlePoll;
            try
            {
                var sweep = await sender.SweepAllTenantsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (sweep.Pending)
                {
                    wait = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The CDN purge sweep failed; it runs again on the next interval");
            }

            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
