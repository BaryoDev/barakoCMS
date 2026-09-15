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
/// fetched by the slug in its URL. This applies the permission check <c>GET /api/contents/{id}</c>
/// applies, to any status, and returns the same response built by <c>EntryResponse</c>, masking
/// included.
///
/// An entry the caller may not read answers 404, not the 403 the id route gives. A slug is readable
/// text a caller can guess, and any signed-in account, a self-registered one with no content
/// permission included, reaches this route, so a 403 would confirm that a draft or another user's
/// entry exists under that slug. An id is not guessable, which is why the id route can say 403.
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

        barakoCMS.Models.Content? content = null;
        if (user != null)
        {
            foreach (var candidate in await FindAsync(req.Type, req.Slug, ct))
            {
                if (await _permissionResolver.CanPerformActionAsync(user, candidate.ContentType, "read", candidate, ct))
                {
                    content = candidate;
                    break;
                }
            }
        }

        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        Response = await EntryResponse.BuildAsync(
            content, _session, _sourcing, Resolve<ISensitivityService>(), HttpContext, ct);
    }

    /// <summary>The entries holding this slug, in the request's tenant, oldest first, at most a handful.</summary>
    /// <remarks>
    /// The slug field and the match are the ones delivery and the uniqueness rule use, so all three
    /// agree on which entry a slug names. Oldest first with the id as tiebreak for the same reason the
    /// public route orders: uniqueness is a check on write (#717), not a constraint, so a deployment
    /// can still hold duplicates (rows from before it, a bulk import, a workflow field update, two
    /// concurrent saves) and should get a stable answer rather than whichever row Postgres returns.
    /// More than one row is returned so that a duplicate the caller cannot read does not hide a newer
    /// one they can. Only the oldest ten are checked: past that a readable duplicate answers 404, and
    /// the bound stays because any signed-in account reaches this route.
    /// </remarks>
    private async Task<IReadOnlyList<barakoCMS.Models.Content>> FindAsync(string type, string slug, CancellationToken ct)
    {
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (def is null) return [];

        var slugField = PublicDelivery.SlugField(def);
        if (slugField is null) return [];

        var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(slugField, slug);

        return await _session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == type && c.MatchesSql(sql, parameters))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(10)
            .ToListAsync(ct);
    }
}
