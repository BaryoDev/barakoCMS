using System.Collections.Concurrent;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// Answers "has this tenant turned purging on" for the commit listener, so a tenant that has not pays
/// nothing on its commits (#561).
/// </summary>
/// <remarks>
/// The default tenant reads host configuration, which costs no round trip. Any other tenant's answer
/// is one read of its settings row, kept for <see cref="Ttl"/>. A settings write on this node forgets
/// the answer at once, and another node picks the change up when its answer expires, so no restart is
/// needed. A failed read answers off and is not kept. "On" means the sender could build a purge from the setup: the
/// same completeness the resolver applies, and a token that decrypts. An invalid setup writes no rows.
/// </remarks>
internal sealed class CdnPurgeGate(IConfiguration configuration, ISecretProtector protector)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool On, DateTime Expires)> _answers = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsOnAsync(IDocumentStore store, string slug, CancellationToken ct)
    {
        if (string.Equals(slug, Tenant.DefaultSlug, StringComparison.OrdinalIgnoreCase))
        {
            return CdnPurgeResolver.HostIsComplete(configuration);
        }

        if (_answers.TryGetValue(slug, out var known) && known.Expires > DateTime.UtcNow)
        {
            return known.On;
        }

        await using var query = store.QuerySession();
        var row = await query.LoadAsync<CdnPurgeSettings>(CdnPurgeSettings.IdForTenant(slug), ct);
        var on = CdnPurgeResolver.RowIsComplete(row) && !string.IsNullOrWhiteSpace(protector.Unprotect(row!.ProtectedToken));
        _answers[slug] = (on, DateTime.UtcNow + Ttl);
        return on;
    }

    public void Forget(string slug) => _answers.TryRemove(slug, out _);
}

/// <summary>Wakes the sender early when this node wrote a pending purge.</summary>
internal sealed class CdnPurgeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Nudge()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already nudged. The sender will look at everything pending when it wakes.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
