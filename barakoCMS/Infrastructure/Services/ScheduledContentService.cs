using Marten;
using barakoCMS.Models;
using barakoCMS.Events;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Background service that applies scheduled publishing. On each sweep it promotes Drafts whose
/// <see cref="Content.ScheduledPublishAt"/> has arrived to Published, and Archives Published items whose
/// <see cref="Content.ScheduledUnpublishAt"/> has arrived. Each transition emits a ContentStatusChanged
/// event (so the async WorkflowProjection fires "Published" workflows and history stays correct) and
/// clears the consumed schedule field.
///
/// Content is conjoined multi-tenant, so unlike the token cleanup this must sweep every tenant: the
/// default partition plus each active tenant in the registry. Sessions are opened straight off the
/// store with an explicit tenant, since a background service has no request-scoped tenant.
/// </summary>
public class ScheduledContentService : BackgroundService
{
    private readonly IDocumentStore _store;
    private readonly ILogger<ScheduledContentService> _logger;

    // A minute is fine granularity for editorial scheduling and keeps the query load trivial (a couple
    // of indexed lookups per tenant per minute).
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    // Transitions the scheduler makes are attributed to the system, not a user.
    public static readonly Guid SystemActor = Guid.Empty;

    public ScheduledContentService(IDocumentStore store, ILogger<ScheduledContentService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduled content service started. Sweep interval: {Interval}", SweepInterval);

        // Let the app (and the Marten schema/daemon) warm up before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAllTenantsAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled content sweep");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }

        _logger.LogInformation("Scheduled content service stopped");
    }

    /// <summary>Sweeps the default partition and every active tenant. Exposed for tests.</summary>
    public async Task SweepAllTenantsAsync(DateTime nowUtc, CancellationToken ct)
    {
        // null slug => the default (no-explicit-tenant) partition, where single-deployment sites like
        // baryo.dev keep their content; named slugs are the path-based tenants.
        var partitions = new List<string?> { null };
        await using (var query = _store.QuerySession())
        {
            var tenants = await query.Query<Tenant>().Where(t => t.IsActive).ToListAsync(ct);
            partitions.AddRange(tenants.Select(t => (string?)t.Slug));
        }

        foreach (var slug in partitions.Distinct())
        {
            await using var session = slug is null ? _store.LightweightSession() : _store.LightweightSession(slug);
            var changed = await SweepTenantAsync(session, nowUtc, ct);
            if (changed > 0)
                _logger.LogInformation("Scheduled sweep applied {Count} transition(s) for tenant {Tenant}",
                    changed, slug ?? "(default)");
        }
    }

    /// <summary>
    /// Applies all due transitions in one tenant session and saves. Returns the number of items flipped.
    /// Pure over the session so tests can drive it directly without the timer.
    /// </summary>
    public static async Task<int> SweepTenantAsync(IDocumentSession session, DateTime nowUtc, CancellationToken ct)
    {
        var due = await session.Query<Content>()
            .Where(c => (c.Status == ContentStatus.Draft
                         && c.ScheduledPublishAt != null && c.ScheduledPublishAt <= nowUtc)
                     || (c.Status == ContentStatus.Published
                         && c.ScheduledUnpublishAt != null && c.ScheduledUnpublishAt <= nowUtc))
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        // Constructed rather than injected: this sweep opens its own session per tenant, so there
        // is no scoped writer to resolve.
        var writer = new ContentWriter(session);

        foreach (var content in due)
        {
            var newStatus = content.Status == ContentStatus.Draft
                ? ContentStatus.Published
                : ContentStatus.Archived;

            writer.Append(content, new ContentStatusChanged(content.Id, newStatus, SystemActor));

            // Clear only the field just consumed; the opposite one stays armed, since a Published
            // item can still carry a future unpublish time. Recorded as an event rather than
            // written straight to the document: consuming a schedule is a state change, and one
            // that happened without a user, so the trail is the only place it is visible.
            writer.Append(
                content,
                newStatus == ContentStatus.Published
                    ? new ContentScheduled(content.Id, null, content.ScheduledUnpublishAt, SystemActor)
                    : new ContentScheduled(content.Id, content.ScheduledPublishAt, null, SystemActor));
        }

        await session.SaveChangesAsync(ct);
        return due.Count;
    }
}
