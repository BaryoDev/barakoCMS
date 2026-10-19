using System.Collections.Concurrent;
using System.Diagnostics;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Remembers, per tenant, that no published <c>site</c> entry is in Private mode, so an anonymous
/// delivery request in a public tenant costs no read for the gate (#1175).
/// </summary>
/// <remarks>
/// Only the "not private" answer is kept. A stale "private" fails closed and costs one read; a stale
/// "not private" would serve a private tenant to anonymous callers, so that is the answer that is
/// invalidated. <see cref="PrivateSiteCacheListener"/> clears it on this node in the same commit that
/// writes a site entry. A read that began before the commit cannot put a stale answer back: it
/// remembers the tenant's generation first and is dropped if the generation moved.
///
/// Another node is not told. Its copy lives for <see cref="TtlKey"/> seconds at most, which is the
/// longest a private tenant can be served to anonymous callers by a node that has not yet seen the
/// change. Set it to 0 to read on every request.
/// </remarks>
internal sealed class PrivateSiteCache(IConfiguration configuration)
{
    public const string TtlKey = "Delivery:PrivateSite:CacheSeconds";
    public const int DefaultTtlSeconds = 5;

    private readonly ConcurrentDictionary<string, State> _tenants = new(StringComparer.Ordinal);

    private long Ttl => (long)(Math.Max(0, configuration.GetValue(TtlKey, DefaultTtlSeconds)) * Stopwatch.Frequency);

    public async Task<Content?> FindPrivateEntryAsync(string tenant, IQuerySession session, CancellationToken ct)
    {
        var ttl = Ttl;
        if (ttl == 0)
        {
            return await PrivateSite.FindPrivateEntryAsync(session, ct);
        }

        var state = _tenants.GetOrAdd(tenant, _ => new State());
        var (generation, until) = state.Read();
        if (Stopwatch.GetTimestamp() < until)
        {
            return null;
        }

        var site = await PrivateSite.FindPrivateEntryAsync(session, ct);
        if (site is null)
        {
            state.RememberPublic(generation, Stopwatch.GetTimestamp() + ttl);
        }

        return site;
    }

    public void Invalidate(string tenant) => _tenants.GetOrAdd(tenant, _ => new State()).Clear();

    private sealed class State
    {
        private readonly object _gate = new();
        private long _generation;
        private long _until;

        public (long Generation, long Until) Read()
        {
            lock (_gate)
            {
                return (_generation, _until);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _generation++;
                _until = 0;
            }
        }

        public void RememberPublic(long generation, long until)
        {
            lock (_gate)
            {
                if (_generation == generation)
                {
                    _until = until;
                }
            }
        }
    }
}
