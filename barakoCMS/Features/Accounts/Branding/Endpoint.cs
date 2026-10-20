using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.Branding;

internal sealed class Response
{
    public Guid AccountId { get; init; }
    public string AccountSlug { get; init; } = string.Empty;

    /// <summary>The account's name, or null when none has been set and the console keeps its own.</summary>
    public string? Name { get; init; }

    public string? LogoUrl { get; init; }
    public Dictionary<string, string> Colours { get; init; } = new();
}

/// <summary>GET /api/accounts/branding: the console branding of the account that owns the caller's current tenant.</summary>
/// <remarks>
/// Open to any signed-in caller, since the console reads it for whoever is using it. An account
/// with no colours set answers the tenant's own <c>Branding</c> dictionary, which is what a console
/// read before accounts existed. A deployment with no accounts answers no name and no logo.
/// </remarks>
internal class Endpoint(IQuerySession session) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Get("/api/accounts/branding");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var slug = (User.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        var accountId = tenant?.AccountId ?? TenantAccount.DefaultId;

        var account = await session.LoadAsync<TenantAccount>(accountId, ct);
        var fallback = Array.Empty<KeyValuePair<string, string>>();
#pragma warning disable CS0618 // read as the fallback until the member goes
        if (tenant?.Branding is { Count: > 0 } legacy)
            fallback = legacy.ToArray();
#pragma warning restore CS0618

        var colours = account?.Colours is { Count: > 0 } set
            ? set
            : new Dictionary<string, string>(fallback);

        await Send.OkAsync(new Response
        {
            AccountId = accountId,
            AccountSlug = account?.Slug ?? TenantAccount.DefaultSlug,
            Name = account?.Name,
            LogoUrl = account?.LogoUrl,
            Colours = colours,
        }, ct);
    }
}
