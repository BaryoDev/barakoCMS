using barakoCMS.Features.Public;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
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
/// favour of an entry share link (<c>POST /api/contents/{id}/share-links</c>), and every answer
/// a signed-in caller gets carries a <c>Deprecation</c> header.
/// </summary>
/// <remarks>
/// The token is the key of an entry share link, so it is stored hashed, listed on the entry, audited
/// and revocable like any other. The caller needs read on the entry, the same check as the authoring
/// read, so a token cannot be had for a draft the caller may not see. The body and the status codes
/// are what they were when the token was a signed JWT.
/// </remarks>
internal class CreatePreviewTokenEndpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    TenantContext tenant) : Endpoint<CreatePreviewTokenRequest, CreatePreviewTokenResponse>
{
    /// <summary>The day the route was deprecated, 2 October 2026, in the form RFC 9745 gives the header.</summary>
    internal const string DeprecatedSince = "@1790899200";

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

        // This route has no answer for a full list, so its links are not capped. What bounds the
        // rows instead: each lasts 30 minutes, and the ones that have run out are removed here.
        session.DeleteWhere<SiteShareLink>(l => l.Preview && l.ExpiresAt < now);

        var key = ShareLinkKeys.NewKey();
        var label = $"Preview of {entry.ContentType}/{PublicDelivery.SlugValue(entry, slugField)}";
        var link = new SiteShareLink
        {
            Id = Guid.NewGuid(),
            Label = label.Length > ShareLinkKeys.MaxLabelLength ? label[..ShareLinkKeys.MaxLabelLength] : label,
            KeyHash = ShareLinkKeys.Hash(key),
            CreatedAt = now,
            CreatedBy = User.FindFirst("Username")?.Value,
            ExpiresAt = now.Add(PreviewToken.DefaultLifetime),
            EntryId = entry.Id,
            Preview = true,
        };
        session.Store(link);

        await AuditLog.RecordAsync(session, tenant.Slug, "site.share_link.created", userId,
            User.FindFirst("Username")?.Value, targetType: nameof(SiteShareLink), targetId: link.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["label"] = link.Label,
                ["expiresAt"] = link.ExpiresAt.ToString("O"),
                ["scope"] = ShareLinkScope.Of(link),
                ["entryId"] = entry.Id.ToString(),
                ["preview"] = true,
            }, ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new CreatePreviewTokenResponse { Token = key, ExpiresAt = link.ExpiresAt.UtcDateTime });
    }
}
