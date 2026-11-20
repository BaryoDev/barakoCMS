namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// What a purge came to. <see cref="Error"/> never carries the API token.
/// </summary>
/// <param name="Succeeded">True when every batch was accepted, and for an empty list.</param>
/// <param name="Status">The HTTP status of the last answer, or null when none came back.</param>
/// <param name="Error">What went wrong, scrubbed of the token. Null on success.</param>
/// <param name="Retryable">True for a network failure, 429 and 5xx, so a caller knows a later try may work.</param>
/// <param name="RetryAfter">What the provider's <c>Retry-After</c> header asked for, when it sent one.</param>
internal sealed record CdnPurgeResult(
    bool Succeeded,
    int? Status,
    string? Error,
    bool Retryable = false,
    TimeSpan? RetryAfter = null)
{
    public static CdnPurgeResult Ok(int? status) => new(true, status, null);
}

/// <summary>Purges cached responses from a CDN by cache tag.</summary>
internal interface ICdnPurger
{
    Task<CdnPurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct);
}
