using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.ApiKeys.Usage;

internal sealed class UsageRequest
{
    public Guid Id { get; set; }
}

internal sealed class UsageValidator : Validator<UsageRequest>
{
    public UsageValidator() => RuleFor(r => r.Id).NotEmpty();
}

internal sealed record RefusalItem(DateTime At, string Reason, string? ContentType);

internal sealed record UsageResponse(
    Guid Id, string Name, DateTime? LastUsedAt, int WindowHours,
    int TotalRefusals, Dictionary<string, int> RefusalsByReason,
    Dictionary<string, int> RefusalsByContentType, List<RefusalItem> RecentRefusals,
    List<string> MissingContentTypes);

/// <summary>
/// GET /api/api-keys/{id}/usage. When the key was last used, why it was refused in the last day
/// (counted in memory, so a restart empties it), and the content types it names that no longer
/// exist. A key of another tenant answers 404, as revoke does.
/// </summary>
internal class KeyUsageEndpoint(IQuerySession session, ApiKeyRefusals refusals) : Endpoint<UsageRequest, UsageResponse>
{
    private const int RecentShown = 20;

    public override void Configure()
    {
        Get("/api/api-keys/{id}/usage");
        Definition.RequireCapability(ListApiKeysEndpoint.Gate.Capability, [.. ListApiKeysEndpoint.Gate.LegacyRoles]);
    }

    public override async Task HandleAsync(UsageRequest req, CancellationToken ct)
    {
        var tenant = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        var key = await session.Query<ApiKey>().FirstOrDefaultAsync(k => k.Id == req.Id && k.TenantSlug == tenant, ct);
        if (key is null) { await Send.NotFoundAsync(ct); return; }

        var named = (key.ContentTypes ?? []).ToList();
        var lowered = named.Select(t => t.ToLowerInvariant()).ToList();
        var existing = named.Count == 0
            ? []
            : (await session.Query<ContentTypeDefinition>().Where(d => lowered.Contains(d.Name.ToLower())).Select(d => d.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recent = refusals.Recent(key.Id);
        await Send.OkAsync(new UsageResponse(
            key.Id, key.Name, key.LastUsedAt, (int)ApiKeyRefusals.Window.TotalHours,
            recent.Count,
            recent.GroupBy(r => ApiKeyRefusals.ReasonName(r.Reason)).ToDictionary(g => g.Key, g => g.Count()),
            recent.Where(r => r.ContentType is not null).GroupBy(r => r.ContentType!).ToDictionary(g => g.Key, g => g.Count()),
            recent.Reverse().Take(RecentShown).Select(r => new RefusalItem(r.At, ApiKeyRefusals.ReasonName(r.Reason), r.ContentType)).ToList(),
            named.Where(t => !existing.Contains(t)).ToList()), ct);
    }
}
