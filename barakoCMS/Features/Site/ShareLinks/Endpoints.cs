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

/// <summary>What a link opens. Read off the stored link, so it cannot say one thing and open another.</summary>
internal static class ShareLinkScope
{
    public const string Site = "site";
    public const string Entry = "entry";
    public const string Page = "page";

    public static string Of(SiteShareLink link) =>
        link.EntryId is null ? Site : link.Path is null ? Entry : Page;
}

internal static class ShareLinkKeys
{
    public const string SiteType = "site";

    public const int MaxLabelLength = 100;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// <see cref="MaxLifetime"/> in whole days, rounded down, which is what the list reports. Rounded
    /// down so the reported number is never past what the create validator allows.
    /// </summary>
    public static int MaxExpiryDays => (int)Math.Floor(MaxLifetime.TotalDays);

    /// <summary>
    /// Active links per tenant, so the list an editor manages stays short. Redemption looks a key up by
    /// its hash, so it does not depend on this: two concurrent creates can pass it, and every link
    /// still redeems.
    /// </summary>
    public const int MaxActive = 100;

    /// <summary>
    /// Active links per entry, for the same reason. Links issued by <c>POST /api/preview</c> are
    /// counted apart, under <see cref="MaxPreviewPerEntry"/>.
    /// </summary>
    public const int MaxActivePerEntry = 20;

    /// <summary>
    /// Live preview tokens per entry. <c>POST /api/preview</c> has no answer for a full list, so
    /// past this it drops the entry's oldest tokens to make room instead of refusing.
    /// </summary>
    public const int MaxPreviewPerEntry = 20;

    /// <summary>Longer than any key this API issues, so an oversized body is not hashed.</summary>
    private const int MaxKeyLength = 256;

    public static string NewKey() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <remarks>
    /// An unsalted fast hash is enough because the key is 32 random bytes the API generated, not
    /// something a person chose, so there is no dictionary to try it against.
    /// </remarks>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The hash an entry, page or preview link is stored under.</summary>
    /// <remarks>
    /// A different hash from a link to the site, so that a lookup by <see cref="Hash"/> cannot find
    /// one. That matters for a build from before entry links, running beside this one or rolled
    /// back to: its redeem finds a row by <see cref="Hash"/> and reads it as leave to show the
    /// whole held site, and it knows nothing of <see cref="SiteShareLink.EntryId"/>.
    ///
    /// The input is one 0xFF byte and then the key. No string encodes to UTF-8 holding 0xFF, so no
    /// value a caller can send hashes to this through <see cref="Hash"/>. A text prefix would not
    /// do: the caller would send the prefix with the key.
    /// </remarks>
    public static string EntryHash(string key)
    {
        var utf8 = Encoding.UTF8.GetBytes(key);
        var input = new byte[utf8.Length + 1];
        input[0] = 0xFF;
        utf8.CopyTo(input, 1);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>The active entry, page or preview link for <paramref name="key"/> in the session's tenant, or null.</summary>
    public static async Task<SiteShareLink?> FindActiveEntryAsync(IQuerySession session, string? key, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return null;
        }

        var hash = EntryHash(key);
        return await session.Query<SiteShareLink>()
            .Where(l => l.KeyHash == hash && l.EntryId != null && l.RevokedAt == null && l.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The active link for <paramref name="key"/> in the session's tenant, looked up as a link to the
    /// site, or null. An entry, page or preview link is stored under <see cref="EntryHash"/> and is
    /// not found here.
    /// </summary>
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

    /// <summary>
    /// The entry an entry or page link opens, projected for anonymous delivery, or null.
    /// </summary>
    /// <remarks>
    /// The link lifts the Published gate for this one entry and nothing else. The projection is the
    /// one every anonymous read goes through, so a type that is not publicly deliverable, an entry
    /// that is not Public and every field that is not Public stay closed. A reference is left as
    /// its id when the target is one delivery serves, and left out otherwise, as on the delivery
    /// routes: the link opens this entry, not the drafts it points at. A file field resolves as it
    /// does on the delivery routes, to a public file or to nothing.
    /// </remarks>
    public static async Task<barakoCMS.Features.Public.PublicContentResponse?> OpenEntryAsync(
        IQuerySession session,
        SiteShareLink link,
        CancellationToken ct,
        barakoCMS.Core.Interfaces.IFileStore? files = null)
    {
        if (link.EntryId is not { } entryId)
        {
            return null;
        }

        var entry = await session.LoadAsync<barakoCMS.Models.Content>(entryId, ct);
        if (entry is null)
        {
            return null;
        }

        var type = entry.ContentType;
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        var projected = barakoCMS.Features.Public.PublicDelivery.ToPublic(
            entry,
            definition,
            definition is null ? null : barakoCMS.Features.Public.PublicDelivery.SlugField(definition),
            allowUnpublished: true);

        if (projected is null || definition is null)
        {
            return projected;
        }

        var resolved = await barakoCMS.Features.Public.PublicFileFields.ResolveAsync([projected], definition, files, ct);
        return (await barakoCMS.Features.Public.PublicReferenceFields.FilterAsync(resolved, definition, session, ct))[0];
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

    /// <summary><c>site</c>, <c>entry</c> or <c>page</c>.</summary>
    public string Scope { get; init; } = ShareLinkScope.Site;

    /// <summary>The path a page link was created for. Null for the other scopes.</summary>
    public string? Path { get; init; }

    public static ShareLinkResponse From(SiteShareLink l) => new()
    {
        Id = l.Id,
        Label = l.Label,
        CreatedAt = l.CreatedAt,
        CreatedBy = l.CreatedBy,
        ExpiresAt = l.ExpiresAt,
        RevokedAt = l.RevokedAt,
        LastUsedAt = l.LastUsedAt,
        Scope = ShareLinkScope.Of(l),
        Path = l.Path,
    };
}

/// <summary>A page of share links, with the rule a console needs before it offers an expiry.</summary>
internal sealed class ShareLinkListResponse : PaginatedResponse<ShareLinkResponse>
{
    /// <summary>
    /// The longest expiry create accepts, in whole days from now. On the page rather than on each
    /// link, so it is there when the tenant has no links yet.
    /// </summary>
    public int MaxExpiryDays { get; init; }
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
        RuleFor(x => x.Label).NotEmpty().MaximumLength(ShareLinkKeys.MaxLabelLength);

        // A minute of slack past the maximum, so a client that asks for the reported maximum from now
        // is not refused because its clock runs a little ahead of this one. The time the request takes
        // only helps: the comparison moves later with it.
        RuleFor(x => x.ExpiresAt!.Value)
            .Must(e => e > DateTimeOffset.UtcNow)
            .WithMessage("expiresAt must be in the future.")
            .Must(e => e <= DateTimeOffset.UtcNow.Add(ShareLinkKeys.MaxLifetime).AddMinutes(1))
            .WithMessage($"expiresAt can be at most {ShareLinkKeys.MaxExpiryDays} days away.")
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
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class CreateShareLinkEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    TenantContext tenant) : Endpoint<CreateShareLinkRequest, CreateShareLinkResponse>
{
    public override void Configure()
    {
        Post("/api/site/share-links");
        Claims("UserId");
    }

    public override async Task HandleAsync(CreateShareLinkRequest req, CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(session, permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var active = await session.Query<SiteShareLink>()
            .CountAsync(l => l.EntryId == null && l.RevokedAt == null && l.ExpiresAt > now, ct);
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
        session.Store(link);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "site.share_link.created", actorId,
            User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["label"] = link.Label,
                ["expiresAt"] = link.ExpiresAt.ToString("O"),
            }, ct: ct);

        await session.SaveChangesAsync(ct);

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
internal sealed class ListShareLinksEndpoint(
    IQuerySession session,
    IPermissionResolver permissions) : Endpoint<PaginatedRequest, ShareLinkListResponse>
{
    public override void Configure()
    {
        Get("/api/site/share-links");
        Claims("UserId");
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(session, permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var total = await session.Query<SiteShareLink>().Where(l => l.EntryId == null).CountAsync(ct);
        var page = await session.Query<SiteShareLink>()
            .Where(l => l.EntryId == null)
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

/// <summary>DELETE /api/site/share-links/{id}: revoke a link. Revoking twice is still 204.</summary>
internal sealed class RevokeShareLinkEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/site/share-links/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await ShareLinkKeys.MayManageAsync(session, permissions, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var link = Guid.TryParse(Route<string>("id"), out var id)
            ? await session.LoadAsync<SiteShareLink>(id, ct)
            : null;

        // An entry's links are revoked through the entry, by whoever may update that entry.
        if (link is null || link.EntryId is not null)
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
                metadata: new Dictionary<string, object> { ["label"] = link.Label }, ct: ct);

            await session.SaveChangesAsync(ct);
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

/// <summary>POST /api/public/site/share-links/redeem: is this key a live link to the whole site for the resolved tenant.</summary>
/// <remarks>
/// 200 or 404 and nothing else. A wrong key, an expired or revoked link, another tenant's key and a
/// key that opens one entry all answer the same 404, so the route says nothing about which links
/// exist. An entry or page key has to be refused here: a frontend reads this 200 as leave to show
/// the whole held site.
/// </remarks>
internal sealed class RedeemShareLinkEndpoint(
    IDocumentSession session) : Endpoint<RedeemShareLinkRequest, RedeemShareLinkResponse>
{
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
        var link = await ShareLinkKeys.FindActiveAsync(session, req.Key, now, ct);
        if (link is not { EntryId: null })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        ShareLinkKeys.RecordUse(session, link, now);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new RedeemShareLinkResponse { ExpiresAt = link.ExpiresAt }, ct);
    }
}
