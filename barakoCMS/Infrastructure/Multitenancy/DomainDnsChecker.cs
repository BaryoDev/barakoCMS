using DnsClient;
using DnsClient.Protocol;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>What a DNS check found for one domain.</summary>
/// <param name="CnameOk">The domain is a CNAME to the deployment's host.</param>
/// <param name="TxtOk">A TXT record at the challenge name holds the claim's value.</param>
/// <param name="TimedOut">The lookups ran out of time or failed, so the answer says nothing about the records.</param>
internal sealed record DomainDnsResult(bool CnameOk, bool TxtOk, bool TimedOut = false)
{
    public bool Passed => CnameOk && TxtOk;
}

/// <summary>Looks up the two records a tenant publishes to prove it controls a domain.</summary>
/// <remarks>
/// Replaceable so tests answer without a network. An implementation honours the token it is given
/// and must not throw for a name that does not resolve: that is a failed check, not an error.
/// </remarks>
internal interface IDomainDnsChecker
{
    /// <param name="domain">The claimed host, as stored.</param>
    /// <param name="cnameTarget">The host the domain has to be a CNAME to.</param>
    /// <param name="txtName">The name that has to carry the TXT record.</param>
    /// <param name="txtValue">What that TXT record has to hold.</param>
    Task<DomainDnsResult> CheckAsync(
        string domain, string cnameTarget, string txtName, string txtValue, CancellationToken ct);
}

/// <summary>The real checker, on DnsClient: System.Net.Dns cannot read TXT or CNAME records.</summary>
/// <remarks>
/// The lookups share one time budget. A resolver that never answers ends the check as timed out
/// rather than holding the request, and an administrator who retries gets a fresh budget. The
/// resolver's cache is off, so the answer to a retry is the record as it is now and not as it was.
/// </remarks>
internal sealed class DnsClientDomainChecker : IDomainDnsChecker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly ILookupClient _lookup;
    private readonly TimeSpan _timeout;

    public DnsClientDomainChecker(ILookupClient? lookup = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _lookup = lookup ?? new LookupClient(new LookupClientOptions
        {
            UseCache = false,
            Retries = 1,
            Timeout = _timeout,
            ThrowDnsErrors = false,
        });
    }

    public async Task<DomainDnsResult> CheckAsync(
        string domain, string cnameTarget, string txtName, string txtValue, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_timeout);

        try
        {
            var cname = await _lookup.QueryAsync(domain, QueryType.CNAME, cancellationToken: budget.Token);
            var cnameOk = cname.Answers.CnameRecords().Any(r => Same(r.CanonicalName.Value, cnameTarget));

            var txt = await _lookup.QueryAsync(txtName, QueryType.TXT, cancellationToken: budget.Token);
            var txtOk = txt.Answers.TxtRecords().Any(r => r.Text.Any(t => string.Equals(t.Trim(), txtValue, StringComparison.Ordinal)));

            return new DomainDnsResult(cnameOk, txtOk);
        }
        catch (Exception ex) when (ex is OperationCanceledException or DnsResponseException or TimeoutException)
        {
            // The caller's own cancellation is theirs to see; only our budget running out is a result.
            ct.ThrowIfCancellationRequested();
            return new DomainDnsResult(false, false, TimedOut: true);
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
