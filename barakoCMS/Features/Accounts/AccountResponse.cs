using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Accounts;

/// <summary>An account as the API describes it, including the console branding it carries.</summary>
internal sealed class AccountResponse
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? LogoUrl { get; init; }
    public Dictionary<string, string> Colours { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }

    public static AccountResponse From(TenantAccount a) => new()
    {
        Id = a.Id,
        Slug = a.Slug,
        Name = a.Name,
        LogoUrl = a.LogoUrl,
        Colours = a.Colours ?? new(),
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)),
    };
}

/// <summary>The implicit default account, which has no stored document until its branding is set.</summary>
internal static class DefaultAccount
{
    public static TenantAccount Implicit() => new()
    {
        Id = TenantAccount.DefaultId,
        Slug = TenantAccount.DefaultSlug,
        Name = "Default",
        CreatedAt = DateTime.UnixEpoch,
    };

    public static async Task<TenantAccount?> FindAsync(IQuerySession session, string slug, CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();
        var stored = await session.Query<TenantAccount>().FirstOrDefaultAsync(a => a.Slug == slug, ct);
        if (stored is not null)
            return stored;

        return slug == TenantAccount.DefaultSlug ? Implicit() : null;
    }
}
