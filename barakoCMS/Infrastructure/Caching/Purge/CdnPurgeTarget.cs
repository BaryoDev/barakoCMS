namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>The provider names a tenant or the host may set.</summary>
internal static class CdnPurgeProvider
{
    public const string None = "none";
    public const string Cloudflare = "cloudflare";
    public const string Fastly = "fastly";

    public static readonly IReadOnlyList<string> All = [None, Cloudflare, Fastly];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Where a purge goes and what it is sent with. The base URL comes from host configuration or a
/// constant, never from a tenant.
/// </summary>
/// <param name="Provider">One of <see cref="CdnPurgeProvider"/>.</param>
/// <param name="TargetId">The Cloudflare zone id or the Fastly service id.</param>
/// <param name="Token">The API token. Never printed by <see cref="ToString"/>.</param>
/// <param name="BaseUrl">The provider's API root, without a trailing slash.</param>
internal sealed record CdnPurgeTarget(string Provider, string TargetId, string Token, string BaseUrl)
{
    public override string ToString() => $"CdnPurgeTarget {{ Provider = {Provider}, TargetId = {TargetId}, Token = [set] }}";
}
