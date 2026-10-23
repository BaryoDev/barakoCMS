namespace BarakoCMS.CdnPurge;

/// <summary>What a purge call came to. An error never carries the API token.</summary>
public sealed record CdnPurgeResult(bool Succeeded, int? Status, string? Error, bool Retryable = false)
{
    public static CdnPurgeResult Ok(int status) => new(true, status, null);
}

/// <summary>Purges cached responses from a CDN by cache tag.</summary>
public interface ICdnPurger
{
    Task<CdnPurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct);
}
