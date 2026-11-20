using System.Diagnostics;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using JasperFx;
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
        var tenants = await TenantsWithRowsAsync(ct);
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

    /// <summary>
    /// The tenants that may hold pending rows. Where the list comes from the registry, a listed tenant may
    /// hold nothing, so it is narrowed before any tenant is visited. A host where no tenant turned purging
    /// on then costs one query a pass, not one per tenant. A failed narrowing logs and keeps the full list,
    /// so one bad query cannot stop every purge.
    /// </summary>
    internal async Task<IReadOnlyList<string>> TenantsWithRowsAsync(CancellationToken ct)
    {
        var tenants = await TenantPartitions.ListAsync(store, configuration, PartitionsWithPendingPurgesSql, ct);
        if (tenants.Count == 0 || !TenantPartitions.ListsFromRegistry(configuration))
        {
            return tenants;
        }

        try
        {
            return TenantPartitions.Enforced(configuration)
                ? await NarrowToSettingsAsync(tenants, ct)
                : await NarrowToRowsAsync(tenants, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Narrowing the tenants with pending CDN purges failed; every listed tenant is visited this pass");
            return tenants;
        }
    }

    /// <summary>Without enforcement the table can be asked directly which tenants hold rows.</summary>
    private async Task<IReadOnlyList<string>> NarrowToRowsAsync(IReadOnlyList<string> tenants, CancellationToken ct)
    {
        var withRows = (await TenantPartitions.FromRowsAsync(store, PartitionsWithPendingPurgesSql, ct)).ToHashSet(StringComparer.Ordinal);
        return tenants.Where(withRows.Contains).ToList();
    }

    /// <summary>
    /// Under enforcement a bare query on the tenanted table cannot see other tenants. The settings are
    /// single tenanted, so one query lists who has a document. A tenant whose settings are off stays, because
    /// its leftover rows still drain. A tenant with no settings document cannot have written a row, because
    /// the gate refuses without one. The default partition stays: it follows the host configuration.
    /// </summary>
    private async Task<IReadOnlyList<string>> NarrowToSettingsAsync(IReadOnlyList<string> tenants, CancellationToken ct)
    {
        await using var query = store.QuerySession();
        var slugs = (await query.Query<CdnPurgeSettings>().Select(x => x.TenantSlug).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tenants.Where(t => t == StorageConstants.DefaultTenantId || slugs.Contains(TenantScopes.SlugFor(t))).ToList();
    }

    /// <summary>Sends one tenant's due rows as one flush. A test drives it without the timer.</summary>
    public async Task<int> SweepTenantAsync(string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var options = CdnPurgeOptions.From(configuration);
        await using var session = store.LightweightSession(tenantId);
        var due = await ClaimDueAsync(session, now, options, ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var tenant = TenantScopes.SlugFor(tenantId);
        var tags = CdnPurgeBounds.Collapse(due.SelectMany(r => r.Tags), tenant, options.MaxTagsPerFlush, out var level);
        _logger.LogInformation(
            "CDN purge flush for tenant {Tenant}: {Rows} pending row(s), {Tags} tag(s), collapse {Level}",
            tenant, due.Count, tags.Count, level);

        await using var query = store.QuerySession();
        var resolver = new CdnPurgeResolver(query, protector, configuration, httpClientFactory, loggerFactory);
        if (await resolver.ResolveAsync(tenant, ct) is not var (target, purger))
        {
            _logger.LogInformation("Tenant {Tenant} has no usable CDN purge setup (off, or not valid); {Rows} pending row(s) dropped", tenant, due.Count);
            await DropAsync(session, due, ct);
            return due.Count;
        }

        var attempt = due.Max(r => r.Attempts) + 1;
        (CdnPurgeResult result, TimeSpan elapsed) call;
        try
        {
            call = await CallAsync(purger, tags, options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping mid call. The claim is let go so the next start sends these rows,
            // not five minutes later.
            await ReleaseAsync(tenantId, due);
            throw;
        }

        session.Store(NewDeliveryRow(target, call.result, tags.Count, attempt, call.elapsed, now));
        return await SettleAsync(session, due, call.result, attempt, (tenant, options, now), ct);
    }

    /// <summary>
    /// Takes the tenant's due rows with a versioned write, or none when another node got them first
    /// or the tenant waits out a backoff. While any row waits for its retry time, newer rows wait with it,
    /// so a provider that just answered 429 or 5xx is not called again for each commit.
    /// </summary>
    private static async Task<IReadOnlyList<PendingCdnPurge>> ClaimDueAsync(
        IDocumentSession session, DateTimeOffset now, CdnPurgeOptions options, CancellationToken ct)
    {
        if (await session.Query<PendingCdnPurge>().AnyAsync(p => p.RetryAt != null && p.RetryAt > now, ct))
        {
            return [];
        }

        var dueBefore = now - options.Debounce;
        var reclaimBefore = now - ClaimTimeout;
        var due = await session.Query<PendingCdnPurge>()
            .Where(p => p.WrittenAt <= dueBefore
                && (p.RetryAt == null || p.RetryAt <= now)
                && (p.ClaimedAt == null || p.ClaimedAt < reclaimBefore))
            .OrderBy(p => p.WrittenAt)
            .Take(RowsPerFlush)
            .ToListAsync(ct);
        foreach (var row in due)
        {
            row.ClaimedAt = now;
            session.Store(row);
        }

        if (due.Count == 0)
        {
            return due;
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ConcurrencyConflict.Is(ex))
        {
            // Another node took them first.
            return [];
        }

        return due;
    }

    private async Task<int> SettleAsync(
        IDocumentSession session, IReadOnlyList<PendingCdnPurge> due, CdnPurgeResult result, int attempt,
        (string Tenant, CdnPurgeOptions Options, DateTimeOffset Now) at, CancellationToken ct)
    {
        if (result.Succeeded || !result.Retryable || attempt >= at.Options.MaxAttempts)
        {
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "CDN purge for tenant {Tenant} gave up after {Attempts} attempt(s), status {Status}; {Rows} pending row(s) dropped",
                    at.Tenant, attempt, result.Status, due.Count);
            }

            await DropAsync(session, due, ct);
            return due.Count;
        }

        var wait = at.Options.BackoffFor(attempt);
        if (result.RetryAfter is { } asked && asked > wait)
        {
            wait = asked < CdnPurgeOptions.MaxBackoff ? asked : CdnPurgeOptions.MaxBackoff;
        }

        foreach (var row in due)
        {
            row.Attempts = attempt;
            row.RetryAt = at.Now + wait;
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

    private async Task ReleaseAsync(string tenantId, IReadOnlyList<PendingCdnPurge> rows)
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var session = store.LightweightSession(tenantId);
            var ids = rows.Select(r => r.Id).ToArray();
            var held = await session.Query<PendingCdnPurge>().Where(p => ids.Contains(p.Id)).ToListAsync(brief.Token);
            foreach (var row in held)
            {
                row.ClaimedAt = null;
                session.Store(row);
            }

            await SaveQuietlyAsync(session, brief.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Letting go of the CDN purge claim on shutdown failed; the rows are taken again after the claim timeout");
        }
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
