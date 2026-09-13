using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using EntryResponse = barakoCMS.Features.Content.Get.EntryResponse;
using Response = barakoCMS.Features.Content.Get.Response;

namespace barakoCMS.Features.Content.GetBySlug;

/// <summary>
/// The authoring read of one entry, addressed by type and slug instead of id.
/// </summary>
/// <remarks>
/// For a signed-in viewer on a public site. <c>GET /api/public/{type}/{slug}</c> is anonymous and
/// serves only published, publicly deliverable entries, so a page gated by a role had no way to be
/// fetched by the slug in its URL. This answers exactly what <c>GET /api/contents/{id}</c> would for
/// the entry the slug names: the same 401, 404 and 403, the same permission check, any status, and
/// the same response built by <c>EntryResponse</c>, masking included.
///
/// Not gated on <c>IsPubliclyDeliverable</c>. That flag decides what anonymous callers get; here the
/// read permission decides.
/// </remarks>
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly IContentSourcingPolicy _sourcing;

    public Endpoint(
        IQuerySession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        IContentSourcingPolicy sourcing)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _sourcing = sourcing;
    }

    public override void Configure()
    {
        Get("/api/contents/by-slug/{type}/{slug}");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);

        var content = await FindAsync(req.Type, req.Slug, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        Response = await EntryResponse.BuildAsync(
            content, _session, _sourcing, Resolve<ISensitivityService>(), HttpContext, ct);
    }

    /// <summary>The entry holding this slug, in the request's tenant, or null.</summary>
    /// <remarks>
    /// The slug field and the match are the ones delivery and the uniqueness rule use, so all three
    /// agree on which entry a slug names. Oldest first with the id as tiebreak for the same reason the
    /// public route orders: uniqueness is enforced going forward (#717), and a deployment that already
    /// held a duplicate should get a stable answer rather than whichever row Postgres returns.
    /// </remarks>
    private async Task<barakoCMS.Models.Content?> FindAsync(string type, string slug, CancellationToken ct)
    {
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (def is null) return null;

        var slugField = PublicDelivery.SlugField(def);
        if (slugField is null) return null;

        var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(slugField, slug);

        return await _session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == type && c.MatchesSql(sql, parameters))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
    }
}
