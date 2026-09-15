using System.Security.Cryptography;
using System.Text;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;
using Marten.Patching;
using Microsoft.AspNetCore.WebUtilities;

namespace barakoCMS.Features.Site.ShareLinks;

internal static class ShareLinkKeys
{
    public const string SiteType = "site";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// Active links per tenant, so the list an editor manages stays short. Redemption looks a key up by
    /// its hash, so it does not depend on this: two concurrent creates can pass it, and every link
    /// still redeems.
    /// </summary>
    public const int MaxActive = 100;

    /// <summary>Longer than any key this API issues, so an oversized body is not hashed.</summary>
    private const int MaxKeyLength = 256;

    public static string NewKey() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <remarks>
    /// An unsalted fast hash is enough because the key is 32 random bytes the API generated, not
    /// something a person chose, so there is no dictionary to try it against.
    /// </remarks>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The active link for <paramref name="key"/> in the session's tenant, or null.</summary>
    /// <remarks>
    /// Looked up by hash through the unique KeyHash index. The lookup is not constant time, and does
    /// not need to be: what it could leak is how much of a SHA-256 matched, which says nothing about
    /// a key made of 32 random bytes.
    /// </remarks>
    public static async Task<SiteShareLink?> FindActiveAsync(IQuerySession session, string? key, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return null;
        }

        var hash = Hash(key);
        return await session.Query<SiteShareLink>()
            .Where(l => l.KeyHash == hash && l.RevokedAt == null && l.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Records a redemption by patching LastUsedAt alone.</summary>
    /// <remarks>
    /// Storing the document read by redeem would write back every field as it was at the read, so a
    /// revoke landing between the read and this write would be undone.
    /// </remarks>
    public static void RecordUse(IDocumentSession session, SiteShareLink link, DateTimeOffset now) =>
        session.Patch<SiteShareLink>(link.Id).Set(x => x.LastUsedAt, now);

    /// <summary>Revokes by patching RevokedAt alone, so a redeem landing after the load keeps its LastUsedAt.</summary>
    public static void Revoke(IDocumentSession session, SiteShareLink link, DateTimeOffset now)
    {
        link.RevokedAt = now;
        session.Patch<SiteShareLink>(link.Id).Set(x => x.RevokedAt, now);
    }

    /// <summary>Whether the caller may manage share links: update permission on the site type.</summary>
    public static async Task<bool> MayManageAsync(
        IQuerySession session, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirst("UserId")?.Value, out var userId))
        {
            return false;
        }

        var user = await session.LoadAsync<User>(userId, ct);
        return user is not null && await permissions.CanPerformActionAsync(user, SiteType, "update", null, ct);
    }
}

internal sealed class ShareLinkResponse
{
    public Guid Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public static ShareLinkResponse From(SiteShareLink l) => new()
    {
        Id = l.Id,
        Label = l.Label,
        CreatedAt = l.CreatedAt,
        CreatedBy = l.CreatedBy,
        ExpiresAt = l.ExpiresAt,
        RevokedAt = l.RevokedAt,
        LastUsedAt = l.LastUsedAt,
    };
}

internal sealed class CreateShareLinkRequest
{
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
}

internal sealed class CreateShareLinkValidator : Validator<CreateShareLinkRequest>
{
    public CreateShareLinkValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);

        // A minute of slack past the maximum, so a client that computes "90 days from now" before
        // sending is not refused for the time the request took.
        RuleFor(x => x.ExpiresAt!.Value)
            .Must(e => e > DateTimeOffset.UtcNow)
            .WithMessage("expiresAt must be in the future.")
            .Must(e => e <= DateTimeOffset.UtcNow.Add(ShareLinkKeys.MaxLifetime).AddMinutes(1))
            .WithMessage("expiresAt can be at most 90 days away.")
            .OverridePropertyName(nameof(CreateShareLinkRequest.ExpiresAt))
            .When(x => x.ExpiresAt.HasValue);
    }
}

internal sealed class CreateShareLinkResponse
{
    public Guid Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The key, in this response and nowhere else.</summary>
    public string Key { get; init; } = string.Empty;
}

/// <summary>POST /api/site/share-links: create a link and return its key once.</summary>
internal sealed class CreateShareLinkEndpoint : Endpoint<CreateShareLinkRequest, CreateShareLinkResponse>
{
    private readonly IDocumentSession _session;
    private readonly IPermissionResolver _permissions;
    private readonly TenantContext _tenant;

    public CreateShareLinkEndpoint(IDocumentSession session, IPermissionResolver permissions, TenantContext tenant)
    {
        _session = session;
        _permissions = permissions;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/site/share-links");
        Claims("UserId");
    }

    public override async Task HandleAsync(CreateShareLinkRequest req, CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(_session, _permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var active = await _session.Query<SiteShareLink>()
            .CountAsync(l => l.RevokedAt == null && l.ExpiresAt > now, ct);
        if (active >= ShareLinkKeys.MaxActive)
        {
            AddError($"This site already has {ShareLinkKeys.MaxActive} active share links. Revoke one first.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var key = ShareLinkKeys.NewKey();
        var link = new SiteShareLink
        {
            Id = Guid.NewGuid(),
            Label = req.Label.Trim(),
            KeyHash = ShareLinkKeys.Hash(key),
            CreatedAt = now,
            CreatedBy = User.FindFirst("Username")?.Value,
            ExpiresAt = req.ExpiresAt?.ToUniversalTime() ?? now.Add(ShareLinkKeys.DefaultLifetime),
        };
        _session.Store(link);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, _tenant.Slug, "site.share_link.created", actorId,
            User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["label"] = link.Label,
                ["expiresAt"] = link.ExpiresAt.ToString("O"),
            }, ct: ct);

        await _session.SaveChangesAsync(ct);

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.ResponseAsync(new CreateShareLinkResponse
        {
            Id = link.Id,
            Label = link.Label,
            ExpiresAt = link.ExpiresAt,
            CreatedAt = link.CreatedAt,
            Key = key,
        }, 201, ct);
    }
}

/// <summary>GET /api/site/share-links: the tenant's links, newest first, without keys or hashes.</summary>
internal sealed class ListShareLinksEndpoint : Endpoint<PaginatedRequest, PaginatedResponse<ShareLinkResponse>>
{
    private readonly IQuerySession _session;
    private readonly IPermissionResolver _permissions;

    public ListShareLinksEndpoint(IQuerySession session, IPermissionResolver permissions)
    {
        _session = session;
        _permissions = permissions;
    }

    public override void Configure()
    {
        Get("/api/site/share-links");
        Claims("UserId");
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(_session, _permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var total = await _session.Query<SiteShareLink>().CountAsync(ct);
        var page = await _session.Query<SiteShareLink>()
            .OrderByDescending(l => l.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await Send.OkAsync(new PaginatedResponse<ShareLinkResponse>
        {
            Items = page.Select(ShareLinkResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, ct);
    }
}

/// <summary>DELETE /api/site/share-links/{id}: revoke a link. Revoking twice is still 204.</summary>
internal sealed class RevokeShareLinkEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;
    private readonly IPermissionResolver _permissions;
    private readonly TenantContext _tenant;

    public RevokeShareLinkEndpoint(IDocumentSession session, IPermissionResolver permissions, TenantContext tenant)
    {
        _session = session;
        _permissions = permissions;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/site/share-links/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(_session, _permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var link = Guid.TryParse(Route<string>("id"), out var id)
            ? await _session.LoadAsync<SiteShareLink>(id, ct)
            : null;
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (link.RevokedAt is null)
        {
            ShareLinkKeys.Revoke(_session, link, DateTimeOffset.UtcNow);

            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(_session, _tenant.Slug, "site.share_link.revoked", actorId,
                User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
                metadata: new Dictionary<string, object> { ["label"] = link.Label }, ct: ct);

            await _session.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}

internal sealed class RedeemShareLinkRequest
{
    public string? Key { get; set; }
}

internal sealed class RedeemShareLinkResponse
{
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>POST /api/public/site/share-links/redeem: is this key a live share link for the resolved tenant.</summary>
/// <remarks>
/// 200 or 404 and nothing else. A wrong key, an expired or revoked link and another tenant's key all
/// answer the same 404, so the route says nothing about which links exist.
/// </remarks>
internal sealed class RedeemShareLinkEndpoint : Endpoint<RedeemShareLinkRequest, RedeemShareLinkResponse>
{
    private readonly IDocumentSession _session;

    public RedeemShareLinkEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/public/site/share-links/redeem");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.SiteSharePolicy));
    }

    public override async Task HandleAsync(RedeemShareLinkRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";

        var now = DateTimeOffset.UtcNow;
        var link = await ShareLinkKeys.FindActiveAsync(_session, req.Key, now, ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        ShareLinkKeys.RecordUse(_session, link, now);
        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(new RedeemShareLinkResponse { ExpiresAt = link.ExpiresAt }, ct);
    }
}
