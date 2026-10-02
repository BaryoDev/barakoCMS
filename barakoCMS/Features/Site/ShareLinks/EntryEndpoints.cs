using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Site.ShareLinks;

internal static class EntryShareLinks
{
    public const string RoutePattern = "/api/contents/{id}/share-links";

    /// <summary>Longer than any path the Pages module resolves, which stops at 2048.</summary>
    public const int MaxPathLength = 2048;

    /// <summary>
    /// A path on the site: one leading slash, and nothing a browser could read as another origin
    /// or as the start of a query or a fragment.
    /// </summary>
    /// <remarks>
    /// The path is handed back to whoever opens the link, and a frontend is expected to send them
    /// there. <c>//host</c> and <c>/\host</c> both leave the site in a browser, so neither is stored.
    /// </remarks>
    public static bool IsSitePath(string? path) =>
        path is { Length: > 0 and <= MaxPathLength }
        && path[0] == '/'
        && (path.Length == 1 || path[1] != '/')
        && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#');

    /// <summary>
    /// The entry named in the route, and whether the caller may manage its links: update permission
    /// on that entry.
    /// </summary>
    /// <remarks>
    /// Update rather than read, which is all <c>POST /api/preview</c> asks for. A link made here can
    /// last 90 days in the hands of someone with no account, so making one is an editor's decision.
    /// </remarks>
    public static async Task<(barakoCMS.Models.Content? Entry, bool Allowed)> FindAsync(
        IQuerySession session,
        IPermissionResolver permissions,
        System.Security.Claims.ClaimsPrincipal principal,
        string? routeId,
        CancellationToken ct)
    {
        var entry = Guid.TryParse(routeId, out var id)
            ? await session.LoadAsync<barakoCMS.Models.Content>(id, ct)
            : null;
        if (entry is null || !Guid.TryParse(principal.FindFirst("UserId")?.Value, out var userId))
        {
            return (entry, false);
        }

        var user = await session.LoadAsync<User>(userId, ct);
        return (entry, user is not null && await permissions.CanPerformActionAsync(user, entry.ContentType, "update", entry, ct));
    }
}

internal sealed class CreateEntryShareLinkRequest
{
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set for a page link: the path the page is served at. Unset for an entry link.</summary>
    public string? Path { get; set; }
}

internal sealed class CreateEntryShareLinkValidator : Validator<CreateEntryShareLinkRequest>
{
    public CreateEntryShareLinkValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(ShareLinkKeys.MaxLabelLength);

        // The same two bounds, and the same minute of slack, as a link to the whole site.
        RuleFor(x => x.ExpiresAt!.Value)
            .Must(e => e > DateTimeOffset.UtcNow)
            .WithMessage("expiresAt must be in the future.")
            .Must(e => e <= DateTimeOffset.UtcNow.Add(ShareLinkKeys.MaxLifetime).AddMinutes(1))
            .WithMessage($"expiresAt can be at most {ShareLinkKeys.MaxExpiryDays} days away.")
            .OverridePropertyName(nameof(CreateEntryShareLinkRequest.ExpiresAt))
            .When(x => x.ExpiresAt.HasValue);

        RuleFor(x => x.Path)
            .Must(EntryShareLinks.IsSitePath)
            .WithMessage($"path must start with one / and hold no whitespace, backslash, ? or #, in at most {EntryShareLinks.MaxPathLength} characters.")
            .When(x => x.Path is not null);
    }
}

internal sealed class CreateEntryShareLinkResponse
{
    public Guid Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Scope { get; init; } = ShareLinkScope.Entry;
    public string? Path { get; init; }

    /// <summary>The key, in this response and nowhere else.</summary>
    public string Key { get; init; } = string.Empty;
}

/// <summary>POST /api/contents/{id}/share-links: create a link to this one entry and return its key once.</summary>
/// <remarks>
/// With <c>path</c> it is a page link, without it an entry link. Both open the same thing, this
/// entry; the path is only carried to whoever opens the link.
/// </remarks>
internal sealed class CreateEntryShareLinkEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    TenantContext tenant) : Endpoint<CreateEntryShareLinkRequest, CreateEntryShareLinkResponse>
{
    public override void Configure()
    {
        Post(EntryShareLinks.RoutePattern);
        Claims("UserId");
    }

    public override async Task HandleAsync(CreateEntryShareLinkRequest req, CancellationToken ct)
    {
        var (entry, allowed) = await EntryShareLinks.FindAsync(session, permissions, User, Route<string>("id"), ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!allowed)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var entryId = entry.Id;
        if (await ShareLinkKeys.OpenEntryAsync(session, new SiteShareLink { EntryId = entryId }, ct) is null)
        {
            AddError("A link to this entry would open nothing: its type is not publicly deliverable, or the entry is not Public.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var active = await session.Query<SiteShareLink>()
            .CountAsync(l => l.EntryId == entryId && !l.Preview && l.RevokedAt == null && l.ExpiresAt > now, ct);
        if (active >= ShareLinkKeys.MaxActivePerEntry)
        {
            AddError($"This entry already has {ShareLinkKeys.MaxActivePerEntry} active share links. Revoke one first.");
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
            EntryId = entryId,
            Path = req.Path,
        };
        session.Store(link);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "site.share_link.created", actorId,
            User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["label"] = link.Label,
                ["expiresAt"] = link.ExpiresAt.ToString("O"),
                ["scope"] = ShareLinkScope.Of(link),
                ["entryId"] = entryId.ToString(),
            }, ct: ct);

        await session.SaveChangesAsync(ct);

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.ResponseAsync(new CreateEntryShareLinkResponse
        {
            Id = link.Id,
            Label = link.Label,
            ExpiresAt = link.ExpiresAt,
            CreatedAt = link.CreatedAt,
            Scope = ShareLinkScope.Of(link),
            Path = link.Path,
            Key = key,
        }, 201, ct);
    }
}

/// <summary>
/// GET /api/contents/{id}/share-links: this entry's links, newest first, without keys or hashes.
/// Links <c>POST /api/preview</c> issued are listed too, so they can be revoked.
/// </summary>
internal sealed class ListEntryShareLinksEndpoint(
    IQuerySession session,
    IPermissionResolver permissions) : Endpoint<PaginatedRequest, ShareLinkListResponse>
{
    public override void Configure()
    {
        Get(EntryShareLinks.RoutePattern);
        Claims("UserId");
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var (entry, allowed) = await EntryShareLinks.FindAsync(session, permissions, User, Route<string>("id"), ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!allowed)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var entryId = entry.Id;
        var total = await session.Query<SiteShareLink>().Where(l => l.EntryId == entryId).CountAsync(ct);
        var page = await session.Query<SiteShareLink>()
            .Where(l => l.EntryId == entryId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await Send.OkAsync(new ShareLinkListResponse
        {
            Items = page.Select(ShareLinkResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
            MaxExpiryDays = ShareLinkKeys.MaxExpiryDays,
        }, ct);
    }
}

/// <summary>
/// DELETE /api/contents/{id}/share-links/{linkId}: revoke one of this entry's links. Revoking twice
/// is still 204. A link that belongs to another entry, or to the whole site, is 404 here.
/// </summary>
internal sealed class RevokeEntryShareLinkEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete(EntryShareLinks.RoutePattern + "/{linkId}");
        Claims("UserId");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (entry, allowed) = await EntryShareLinks.FindAsync(session, permissions, User, Route<string>("id"), ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!allowed)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var link = Guid.TryParse(Route<string>("linkId"), out var linkId)
            ? await session.LoadAsync<SiteShareLink>(linkId, ct)
            : null;
        if (link is null || link.EntryId != entry.Id)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (link.RevokedAt is null)
        {
            ShareLinkKeys.Revoke(session, link, DateTimeOffset.UtcNow);

            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(session, tenant.Slug, "site.share_link.revoked", actorId,
                User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["label"] = link.Label,
                    ["scope"] = ShareLinkScope.Of(link),
                    ["entryId"] = entry.Id.ToString(),
                }, ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}

internal sealed class OpenShareLinkRequest
{
    public string? Key { get; set; }
}

internal sealed class OpenShareLinkResponse
{
    /// <summary><c>site</c>, <c>entry</c> or <c>page</c>.</summary>
    public string Scope { get; init; } = ShareLinkScope.Site;

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>For a page link, the path it was created for.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The one entry an entry or page link opens, whatever its status, in the shape public delivery
    /// returns. Null for a link to the whole site, which opens nothing the API holds back.
    /// </summary>
    public PublicContentResponse? Entry { get; init; }
}

/// <summary>
/// POST /api/public/site/share-links/open: what this key opens for the resolved tenant, and for an
/// entry or page link the entry itself.
/// </summary>
/// <remarks>
/// The key travels in the body, so it is in no URL, no access log and no Referer. 200 or 404 and
/// nothing else: a wrong key, an expired or revoked link, another tenant's key and a link whose
/// entry is gone or can no longer be delivered all answer the same 404. The request names no entry,
/// so there is no id for a caller to change.
/// </remarks>
internal sealed class OpenShareLinkEndpoint(
    IDocumentSession session) : Endpoint<OpenShareLinkRequest, OpenShareLinkResponse>
{
    public override void Configure()
    {
        Post("/api/public/site/share-links/open");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(RateLimitSetup.SiteSharePolicy));
    }

    public override async Task HandleAsync(OpenShareLinkRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";

        var now = DateTimeOffset.UtcNow;
        var link = await ShareLinkKeys.FindActiveAsync(session, req.Key, now, ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        PublicContentResponse? entry = null;
        if (link.EntryId is not null)
        {
            entry = await ShareLinkKeys.OpenEntryAsync(session, link, ct);
            if (entry is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        ShareLinkKeys.RecordUse(session, link, now);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new OpenShareLinkResponse
        {
            Scope = ShareLinkScope.Of(link),
            ExpiresAt = link.ExpiresAt,
            Path = link.Path,
            Entry = entry,
        }, ct);
    }
}
