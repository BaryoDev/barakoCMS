using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Counts anonymous lookups of one reference, whichever address they come from.
/// </summary>
/// <remarks>
/// <para>
/// The per visitor limit (<see cref="RateLimitSetup.LookupPolicy"/>) slows one address guessing
/// many references. This slows many addresses guessing the identifier for one reference, which is
/// the attack on a known registration number. Every attempt counts, found or not, so a reference
/// nobody holds is throttled exactly like one somebody does, and the 429 says nothing about which.
/// </para>
/// <para>
/// The cost is that anyone can use up a reference's attempts for a while. That is the same trade as
/// account lockout, and the window is short. Counted in memory per instance, like every other limit
/// here. The key is a hash of the tenant, type and reference, so the limiter holds no reference.
/// </para>
/// </remarks>
internal sealed class LookupReferenceThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public LookupReferenceThrottle(RateLimitWindow window)
    {
        RetryAfterSeconds = window.WindowSeconds;
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = window.PermitLimit,
                Window = TimeSpan.FromSeconds(window.WindowSeconds),
                QueueLimit = 0,
            }));
    }

    /// <summary>The longest a refused caller waits for the window to reopen.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>Takes one attempt for this reference, or returns false when it has none left.</summary>
    public bool TryAcquire(string tenant, string type, string reference)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenant}\u001f{type.ToLowerInvariant()}\u001f{reference.Trim().ToLowerInvariant()}")));

        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
