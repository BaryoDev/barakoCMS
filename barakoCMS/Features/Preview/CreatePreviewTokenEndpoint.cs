using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Preview;

public class CreatePreviewTokenRequest
{
    public string Type { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
}

public class CreatePreviewTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    /// <summary>The query-string parameter to hang the token on: <c>{siteUrl}/{type}/{slug}?preview=&lt;token&gt;</c>.</summary>
    public string QueryParam { get; set; } = PreviewToken.QueryParam;
}

/// <summary>
/// POST /api/preview — an authenticated editor mints a short-lived preview token for one draft entry.
/// The caller must actually have read access to that content type (same check as the authoring read
/// endpoint), so you can only mint a token for a draft you're allowed to see. The token is bound to the
/// current tenant + this type + slug; the public delivery endpoint validates it before revealing a draft.
/// </summary>
public class CreatePreviewTokenEndpoint : Endpoint<CreatePreviewTokenRequest, CreatePreviewTokenResponse>
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;
    private readonly IPermissionResolver _permissions;
    private readonly TenantContext _tenant;

    public CreatePreviewTokenEndpoint(
        IQuerySession session, IConfiguration config, IPermissionResolver permissions, TenantContext tenant)
    {
        _session = session;
        _config = config;
        _permissions = permissions;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/preview"); // authenticated by default
    }

    public override async Task HandleAsync(CreatePreviewTokenRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var user = await _session.LoadAsync<User>(userId, ct);
        if (user is null) { await Send.UnauthorizedAsync(ct); return; }

        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == req.Type, ct);
        var slugField = def is null ? null : PublicDelivery.SlugField(def);
        if (slugField is null) { await Send.NotFoundAsync(ct); return; }

        /* Find the entry by slug across ALL statuses — the whole point is to preview a draft. */
        var candidates = await _session.Query<Models.Content>()
            .Where(c => c.ContentType == req.Type)
            .ToListAsync(ct);
        var entry = candidates.FirstOrDefault(c =>
            string.Equals(PublicDelivery.SlugValue(c, slugField), req.Slug, StringComparison.OrdinalIgnoreCase));

        /* Only someone who can read this content type may mint a preview link. Return 404 for both
         * "no such entry" and "not allowed", so this endpoint isn't a draft-existence oracle for slugs. */
        if (entry is null || !await _permissions.CanPerformActionAsync(user, req.Type, "read", entry, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (token, expiresAt) = PreviewToken.Create(_config, _tenant.Slug, req.Type, req.Slug, entry.Id);
        await Send.ResponseAsync(new CreatePreviewTokenResponse { Token = token, ExpiresAt = expiresAt });
    }
}
