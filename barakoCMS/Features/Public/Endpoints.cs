using barakoCMS.Models;
using FastEndpoints;
using Marten;
using ContentDoc = barakoCMS.Models.Content; /* distinct alias; avoids the Features.Content namespace clash */

namespace barakoCMS.Features.Public;

/*
 * The public delivery surface: anonymous, published-only, slug-addressable, cacheable reads for a
 * website frontend. Distinct from /api/contents (the authenticated authoring API, which returns
 * drafts and permission-filters per user). Here there is no user: the tenant is resolved from the
 * X-Tenant header or host as usual, and delivery is deliberately self-contained about what is public:
 *
 *   - only Status == Published,
 *   - only document Sensitivity == Public (a Sensitive/Hidden document is never delivered), and
 *   - any field the content type marks non-Public is stripped from the payload.
 *
 * It does NOT depend on the ISensitivityService masking mode (which governs the authoring API and can
 * be turned off). A public endpoint must be safe regardless of that setting.
 */

public sealed record PublicContentResponse(
    Guid Id,
    string ContentType,
    string? Slug,
    Dictionary<string, object> Data,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal static class PublicDelivery
{
    /// <summary>
    /// The field holding an entry's slug: a field of type "slug", else a field literally named "slug"
    /// (case-insensitive). Null if the type has no slug field, so it isn't slug-addressable.
    /// </summary>
    public static string? SlugField(ContentTypeDefinition def)
    {
        var byType = def.Fields.FirstOrDefault(f => string.Equals(f.Type, "slug", StringComparison.OrdinalIgnoreCase));
        if (byType is not null) return byType.Name;
        return def.Fields.FirstOrDefault(f => string.Equals(f.Name, "slug", StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public static string? SlugValue(ContentDoc c, string? slugField) =>
        slugField is not null && c.Data.TryGetValue(slugField, out var v) ? v?.ToString() : null;

    /// <summary>
    /// Whether anonymous delivery may serve this type at all. An unknown type and a type that has
    /// not opted in are treated identically on purpose: answering differently would confirm which
    /// types exist.
    /// </summary>
    public static bool IsDeliverable(ContentTypeDefinition? def) => def is { IsPubliclyDeliverable: true };

    public static Dictionary<string, object> PublicData(
        ContentDoc c,
        ContentTypeDefinition? def)
    {
        if (def is null)
            return new();

        var publicNames = def.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return c.Data
            .Where(kv => publicNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Projects a Published, document-Public entry for anonymous delivery, exposing ONLY the fields the
    /// content type marks Public, or null if it must not be exposed at all. Robust to a missing content
    /// type definition: with no schema to say which fields are Public, nothing is delivered (fail closed).
    /// </summary>
    public static PublicContentResponse? ToPublic(ContentDoc c, ContentTypeDefinition? def, string? slugField, bool allowUnpublished = false)
    {
        /* Draft preview (allowUnpublished) skips ONLY the Published gate — a valid, tenant-scoped
         * preview token has already authorized it. The document-Sensitivity gate and the field
         * allowlist below still apply, so a preview never exposes a Sensitive doc or a non-Public field. */
        if (!allowUnpublished && c.Status != ContentStatus.Published) return null;
        if (c.Sensitivity != SensitivityLevel.Public) return null; /* doc-level: never public */
        if (def is null) return null;                              /* no schema -> fail closed */
        /* Type-level opt-in. Endpoints refuse an un-opted-in type outright; this is the backstop, so
         * that a delivery path added later cannot leak by forgetting the check. */
        if (!def.IsPubliclyDeliverable) return null;

        /*
         * Allowlist, not denylist: emit only keys that match a schema field explicitly marked Public.
         * A denylist ("start with all Data, remove the sensitive ones") fails open on any key with no
         * matching current field — an orphan left by a renamed/removed field, or a value stored under a
         * differently-cased key than the schema (validation matches names case-insensitively) — and
         * would leak it. Case-insensitive comparison closes the casing gap too.
         */
        var publicNames = def.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var data = c.Data
            .Where(kv => publicNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new PublicContentResponse(c.Id, c.ContentType, SlugValue(c, slugField), data, c.CreatedAt, c.UpdatedAt);
    }

    /*
     * Short cache window: long enough for a CDN to absorb bursts, short enough that a publish shows up
     * quickly. A publish-triggered rebuild (SSG) is the real freshness mechanism.
     */
    public static void SetCache(HttpContext http) =>
        http.Response.Headers.CacheControl = "public, max-age=60";
}

public sealed class PublicListRequest : PaginatedRequest { }

/// <summary>GET /api/public/{type} — paged Published entries of a content type, masked and cacheable.</summary>
public class ListPublishedEndpoint : Endpoint<PublicListRequest, PaginatedResponse<PublicContentResponse>>
{
    private readonly IQuerySession _session;
    public ListPublishedEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/public/{type}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PublicListRequest req, CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (!PublicDelivery.IsDeliverable(def)) { await SendNotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);

        /* Published + document-Public only; the DB filters the rest out. */
        var baseQuery = _session.Query<ContentDoc>()
            .Where(c => c.ContentType == type
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public);

        var total = await baseQuery.CountAsync(ct);
        var page = await baseQuery
            .OrderByDescending(c => c.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        var items = page
            .Select(c => PublicDelivery.ToPublic(c, def, slugField))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        PublicDelivery.SetCache(HttpContext);
        await SendAsync(new PaginatedResponse<PublicContentResponse>
        {
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}

public sealed record PublicSearchResponse(IReadOnlyList<PublicContentResponse> Results, int Count, string Query);

/// <summary>
/// GET /api/public/{type}/search?q=…&amp;limit=… — top public matches for a query. The literal "search"
/// segment wins over the {slug} route. Matching runs ONLY over allowlisted public fields (the entry is
/// projected to its public shape first), so a draft, a document-Sensitive entry, or a non-Public field
/// can never surface a result. A title/name hit outranks a body hit. Scans a bounded, recent window;
/// swap in Postgres full-text search for larger corpora.
/// </summary>
public class PublicSearchEndpoint : EndpointWithoutRequest<PublicSearchResponse>
{
    private readonly IQuerySession _session;
    public PublicSearchEndpoint(IQuerySession session) => _session = session;

    private const int MaxResults = 50;

    public override void Configure()
    {
        Get("/api/public/{type}/search");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var q = (Query<string>("q", isRequired: false) ?? string.Empty).Trim();
        var limit = Math.Clamp(Query<int?>("limit", isRequired: false) ?? 20, 1, MaxResults);

        /* Eligibility first. Answering the short-query case before this gate returned 200 for a type
         * that is not deliverable, which confirms the type exists — the existence oracle the 404 is
         * meant to close. */
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (!PublicDelivery.IsDeliverable(def)) { await SendNotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);

        if (q.Length < 2)
        {
            await SendOkAsync(new PublicSearchResponse(Array.Empty<PublicContentResponse>(), 0, q), ct);
            return;
        }

        var candidates = await _session.Query<ContentDoc>()
            .Where(c => c.ContentType == type
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public
                        && c.SearchText.ToLower().Contains(q.ToLower()))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var results = candidates
            .Select(c => PublicDelivery.ToPublic(c, def, slugField)) /* project first: only public fields remain */
            .Where(r => r is not null).Select(r => r!)
            .Select(r => new { r, score = Score(r, q) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => x.r)
            .ToList();

        PublicDelivery.SetCache(HttpContext);
        await SendOkAsync(new PublicSearchResponse(results, results.Count, q), ct);
    }

    private static int Score(PublicContentResponse r, string q)
    {
        var score = 0;
        foreach (var (key, value) in r.Data)
        {
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            if (text.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var isTitle = key.Equals("Title", StringComparison.OrdinalIgnoreCase)
                          || key.Equals("Name", StringComparison.OrdinalIgnoreCase);
            score += isTitle ? 10 : 1;
        }
        return score;
    }
}

/// <summary>
/// GET /api/public/{type}/{slug} — a single Published entry by slug; 404 if draft/archived/sensitive/missing.
/// With a valid <c>?preview=&lt;token&gt;</c> (see <see cref="barakoCMS.Infrastructure.Preview.PreviewToken"/>),
/// an unpublished entry is returned too — the token authorizes only that one entry, and the response is
/// still projected to Public fields and marked no-store. An invalid token falls back to published-only.
/// </summary>
public class GetBySlugEndpoint : EndpointWithoutRequest<PublicContentResponse>
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public GetBySlugEndpoint(IQuerySession session, IConfiguration config, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _config = config;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Get("/api/public/{type}/{slug}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var slug = Route<string>("slug") ?? string.Empty;

        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (!PublicDelivery.IsDeliverable(def)) { await SendNotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);
        if (slugField is null) { await SendNotFoundAsync(ct); return; } /* not slug-addressable */

        /* A preview token valid for exactly this tenant+type+slug identifies ONE entry by id, lifting only
         * the Published gate. Binding to the id (not just the slug) means a duplicate-slug draft can't be
         * substituted for the one the token was minted for. */
        var previewToken = Query<string>(barakoCMS.Infrastructure.Preview.PreviewToken.QueryParam, isRequired: false);
        var previewId = string.IsNullOrEmpty(previewToken)
            ? null
            : barakoCMS.Infrastructure.Preview.PreviewToken.ValidatedEntryId(_config, previewToken!, _tenant.Slug, type, slug);

        ContentDoc? match;
        if (previewId is Guid id)
        {
            /* Serve exactly the authorized entry (tenant-scoped session), and re-check it still matches. */
            var entry = await _session.LoadAsync<ContentDoc>(id, ct);
            match = entry is not null
                    && entry.ContentType == type
                    && string.Equals(PublicDelivery.SlugValue(entry, slugField), slug, StringComparison.OrdinalIgnoreCase)
                ? entry : null;
        }
        else
        {
            var candidates = await _session.Query<ContentDoc>()
                .Where(c => c.ContentType == type
                            && c.Status == ContentStatus.Published
                            && c.Sensitivity == SensitivityLevel.Public)
                .ToListAsync(ct);
            match = candidates.FirstOrDefault(c =>
                string.Equals(PublicDelivery.SlugValue(c, slugField), slug, StringComparison.OrdinalIgnoreCase));
        }

        var projected = match is null ? null : PublicDelivery.ToPublic(match, def, slugField, allowUnpublished: previewId is not null);
        if (projected is null) { await SendNotFoundAsync(ct); return; }

        if (previewId is not null)
            HttpContext.Response.Headers.CacheControl = "no-store"; /* never cache a draft */
        else
            PublicDelivery.SetCache(HttpContext);
        await SendOkAsync(projected, ct);
    }
}
