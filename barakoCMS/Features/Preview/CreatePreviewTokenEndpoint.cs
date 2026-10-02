using barakoCMS.Features.Public;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Preview;

internal class CreatePreviewTokenRequest
{
    public string Type { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

internal class CreatePreviewTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    /// <summary>The query-string parameter to hang the token on: <c>{siteUrl}/{type}/{slug}?preview=&lt;token&gt;</c>.</summary>
    public string QueryParam { get; set; } = PreviewToken.QueryParam;
}

/// <summary>
/// POST /api/preview: a signed-in editor gets a 30 minute token for one draft entry. Deprecated in
/// favour of an entry share link (<c>POST /api/contents/{id}/share-links</c>).
/// </summary>
/// <remarks>
/// The token is the key of an entry share link stored with <see cref="SiteShareLink.Preview"/> set:
/// hashed, looked up in the caller's tenant, expired by the server and deleted with its entry. The
/// caller needs read on the entry, the same check as the authoring read, so a token cannot be had
/// for a draft the caller may not see. The body and the status codes are what they were when the
/// token was a signed JWT.
///
/// Read is a low bar and the route used to store nothing, so a token leaves as little behind as it
/// can: a fixed label, no audit row, no place in the entry's list of links, and at most
/// <see cref="ShareLinkKeys.MaxPreviewPerEntry"/> live per entry, the oldest dropped to make room.
///
/// The <c>Deprecation</c> header is set here, so it is on what this handler answers: the 200, its
/// 404s and the 401 for a token whose user is gone. It is not on an answer written before the
/// handler runs: the 401 for no credentials, a 400 from binding, a 429, or the 403 an API key gets.
/// </remarks>
internal class CreatePreviewTokenEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions) : Endpoint<CreatePreviewTokenRequest, CreatePreviewTokenResponse>
{
    /// <summary>The day the route was deprecated, 2 October 2026, in the form RFC 9745 gives the header.</summary>
    internal const string DeprecatedSince = "@1790899200";

    /// <summary>Fixed, so the stored row copies nothing from the entry.</summary>
    internal const string PreviewLabel = "Preview token";

    public override void Configure()
    {
        Post("/api/preview"); // authenticated by default
    }

    public override async Task HandleAsync(CreatePreviewTokenRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers["Deprecation"] = DeprecatedSince;

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var user = await session.LoadAsync<User>(userId, ct);
        if (user is null) { await Send.UnauthorizedAsync(ct); return; }

        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == req.Type, ct);
        var slugField = def is null ? null : PublicDelivery.SlugField(def);
        if (slugField is null) { await Send.NotFoundAsync(ct); return; }

        /* Find the entry by slug across ALL statuses — the whole point is to preview a draft. */
        var candidates = await session.Query<Models.Content>()
            .Where(c => c.ContentType == req.Type)
            .ToListAsync(ct);
        var entry = candidates.FirstOrDefault(c =>
            string.Equals(PublicDelivery.SlugValue(c, slugField), req.Slug, StringComparison.OrdinalIgnoreCase));

        /* Only someone who can read this content type may mint a preview link. Return 404 for both
         * "no such entry" and "not allowed", so this endpoint isn't a draft-existence oracle for slugs. */
        if (entry is null || !await permissions.CanPerformActionAsync(user, req.Type, "read", entry, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var entryId = entry.Id;

        // The tenant's expired tokens go, and so do this entry's oldest live ones past the cap.
        session.DeleteWhere<SiteShareLink>(l => l.Preview && l.ExpiresAt < now);
        var live = await session.Query<SiteShareLink>()
            .Where(l => l.EntryId == entryId && l.Preview && l.ExpiresAt >= now)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);
        foreach (var oldest in live.Take(Math.Max(0, live.Count - ShareLinkKeys.MaxPreviewPerEntry + 1)))
        {
            session.Delete(oldest);
        }

        var key = ShareLinkKeys.NewKey();
        var link = new SiteShareLink
        {
            Id = Guid.NewGuid(),
            Label = PreviewLabel,
            KeyHash = ShareLinkKeys.EntryHash(key),
            CreatedAt = now,
            CreatedBy = User.FindFirst("Username")?.Value,
            ExpiresAt = now.Add(PreviewToken.DefaultLifetime),
            EntryId = entryId,
            Preview = true,
        };
        session.Store(link);

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new CreatePreviewTokenResponse { Token = key, ExpiresAt = link.ExpiresAt.UtcDateTime });
    }
}
