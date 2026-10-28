using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Push;

public sealed class PushSender(
    IDocumentStore store, IPushTransport transport, PushOptions options, ILogger<PushSender> logger) : IPushSender
{
    private static readonly PushSendResult Disabled = new(0, 0, 0, 0, true);

    public Task<PushSendResult> SendToUsersAsync(
        string? tenant, IReadOnlyCollection<Guid> userIds, PushNotification n, CancellationToken ct)
    {
        if (!options.IsConfigured) return Task.FromResult(Disabled);
        if (userIds.Count == 0) return Task.FromResult(new PushSendResult(0, 0, 0, 0, false));
        var ids = userIds.ToArray();
        return SendAsync(tenant, n, q => q.Where(x => ids.Contains(x.UserId)), ct);
    }

    public Task<PushSendResult> SendToTenantAsync(string? tenant, PushNotification n, CancellationToken ct) =>
        options.IsConfigured
            ? SendAsync(tenant, n, q => q, ct)
            : Task.FromResult(Disabled);

    private async Task<PushSendResult> SendAsync(
        string? tenant, PushNotification n,
        Func<IQueryable<StoredSubscription>, IQueryable<StoredSubscription>> filter, CancellationToken ct)
    {
        var payload = n.ToJson();

        await using var session = string.IsNullOrEmpty(tenant) || tenant == Tenant.DefaultSlug
            ? store.LightweightSession()
            : store.LightweightSession(tenant);
        var rows = await filter(session.Query<StoredSubscription>()).ToListAsync(ct);

        int delivered = 0, pruned = 0, failed = 0;
        string? errorType = null;
        foreach (var row in rows)
        {
            if (!options.IsAllowedEndpoint(row.Endpoint))
            {
                failed++;
                errorType = "DisallowedEndpoint";
                continue;
            }

            int status;
            try
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(options.SendTimeout);
                status = await transport.SendAsync(row, payload, sendCts.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                errorType = ex.GetType().Name;
                continue;
            }

            if (status is >= 200 and < 300) delivered++;
            else if (status is 404 or 410)
            {
                session.Delete(row);
                pruned++;
            }
            else
            {
                failed++;
                errorType = "HttpStatus";
            }
        }

        await session.SaveChangesAsync(ct);

        if (failed > 0)
            logger.LogWarning("Push send: {Attempted} attempted, {Delivered} delivered, {Pruned} pruned, {Failed} failed ({ErrorType})",
                rows.Count, delivered, pruned, failed, errorType ?? "Unknown");
        else
            logger.LogInformation("Push send: {Attempted} attempted, {Delivered} delivered, {Pruned} pruned, {Failed} failed",
                rows.Count, delivered, pruned, failed);

        return new PushSendResult(rows.Count, delivered, pruned, failed, false);
    }
}
