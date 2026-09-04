using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
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

internal sealed record PublicContentResponse(
    Guid Id,
    string ContentType,
    string? Slug,
    Dictionary<string, object> Data,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,

    /// <summary>
    /// The resolved SEO metadata, or null when the content type has not opted in.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than left to the caller, which is the point of it existing: the raw
    /// fields are already in Data, and a frontend reading them itself would have to know the field
    /// names and re-implement the fallback. Two frontends would do it two ways and one of them would
    /// emit an empty title tag.
    ///
    /// Null rather than an empty object for a type that has not opted in, so it is absent from the
    /// JSON entirely and a caller cannot mistake "this type has no SEO fields" for "this entry has
    /// not filled them in".
    /// </remarks>
    barakoCMS.Features.Seo.SeoMetadata? Seo = null,

    /// <summary>
    /// Kilometres from the centre of the request's near filter, two decimals. Absent without one.
    /// </summary>
    /// <remarks>
    /// Great-circle distance, the same number the rows were filtered and ordered by. Left out of
    /// the JSON entirely when there is no near filter, so a frontend can test for the key rather
    /// than for null.
    /// </remarks>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? DistanceKm = null);

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


    /// <summary>
    /// Replaces reference ids with the referenced entries, for the fields a caller named in
    /// <c>?include=</c>.
    /// </summary>
    /// <remarks>
    /// One batched load for all of them, which is the entire point: without this every consumer
    /// fetches each reference separately and a list of twenty entries is twenty-one requests.
    ///
    /// Every resolved entry goes through <see cref="ToPublic"/>, the same projection the list itself
    /// uses. That is deliberate and it is what makes this safe: published state, document
    /// sensitivity, type opt-in and the field allowlist are all enforced by that one function, so
    /// resolving cannot become a second way into a Draft. Reimplementing those four checks here
    /// would be the obvious way to get this wrong.
    ///
    /// A target that does not survive the projection has its field removed rather than left as an
    /// id. Leaving the id would say "there is something here you may not see", and removing it
    /// makes an unreadable target indistinguishable from no reference at all.
    /// </remarks>
    public static async Task<List<PublicContentResponse>> ResolveIncludesAsync(
        IReadOnlyList<PublicContentResponse> items,
        IReadOnlyList<string> includeFields,
        ContentTypeDefinition def,
        IQuerySession session,
        CancellationToken ct)
    {
        if (includeFields.Count == 0 || items.Count == 0)
            return items.ToList();

        var ids = new HashSet<Guid>();
        foreach (var item in items)
        foreach (var field in includeFields)
        {
            if (item.Data.TryGetValue(field, out var raw)
                && Guid.TryParse(raw?.ToString(), out var id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
            return items.ToList();

        var idList = ids.ToArray();
        var targets = await session.Query<ContentDoc>()
            .Where(c => c.Id.In(idList))
            .ToListAsync(ct);

        // Each target's own type decides how it is projected, not the referring type's. A reference
        // field names one target type, so this is usually one lookup, but resolving against the
        // wrong schema would apply the wrong field allowlist and that is a leak rather than a bug.
        var typeNames = targets.Select(t => t.ContentType).Distinct().ToList();
        var defs = (await session.Query<ContentTypeDefinition>()
                .Where(d => d.Name.In(typeNames.ToArray()))
                .ToListAsync(ct))
            .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<Guid, PublicContentResponse>();
        foreach (var target in targets)
        {
            defs.TryGetValue(target.ContentType, out var targetDef);
            var projected = ToPublic(target, targetDef, targetDef is null ? null : SlugField(targetDef));
            if (projected is not null)
                resolved[target.Id] = projected;
        }

        return items.Select(item =>
        {
            var data = new Dictionary<string, object>(item.Data);
            foreach (var field in includeFields)
            {
                if (!data.TryGetValue(field, out var raw)) continue;
                if (!Guid.TryParse(raw?.ToString(), out var id)) continue;

                if (resolved.TryGetValue(id, out var target))
                    data[field] = target;
                else
                    data.Remove(field);
            }

            return item with { Data = data };
        }).ToList();
    }

    /// <summary>
    /// The reference fields a caller asked to resolve, or the reason the request is refused.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored, for the same reason an unknown filter is. A silently dropped
    /// include returns ids where the caller expected objects, and nothing in the response says why.
    ///
    /// Naming a field that exists but is not a reference is also refused. It is a mistake either
    /// way, and answering differently for "not a reference" and "does not exist" would say which
    /// non-public fields a type has.
    /// </remarks>
    public static (List<string> Fields, string? Error) ParseIncludes(string? include, ContentTypeDefinition def)
    {
        if (string.IsNullOrWhiteSpace(include))
            return ([], null);

        var referenceFields = def.Fields
            .Where(f => string.Equals(f.Type, "reference", StringComparison.OrdinalIgnoreCase)
                        && f.Sensitivity == SensitivityLevel.Public)
            .ToDictionary(f => f.Name, f => f.Name, StringComparer.OrdinalIgnoreCase);

        var asked = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (asked.Length > MaxIncludes)
            return ([], $"Too many includes. At most {MaxIncludes} are allowed per request.");

        var fields = new List<string>();
        foreach (var name in asked)
        {
            if (!referenceFields.TryGetValue(name, out var canonical))
            {
                var names = referenceFields.Count == 0
                    ? "(none)"
                    : string.Join(", ", referenceFields.Values.OrderBy(x => x, StringComparer.Ordinal));
                return ([], $"Field '{name}' is not a resolvable reference. Resolvable fields: {names}.");
            }

            fields.Add(canonical);
        }

        return (fields, null);
    }

    /// <summary>One batched load per request regardless, but a cap keeps the response bounded.</summary>
    public const int MaxIncludes = 5;

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

        // Resolved off the projected data, not the document, so a field the type marked non-Public
        // cannot reach a frontend through this block after being scrubbed out of Data.
        var seo = barakoCMS.Features.Seo.SeoFields.IsOptedIn(def)
            ? barakoCMS.Features.Seo.SeoFields.Resolve(data)
            : null;

        return new PublicContentResponse(
            c.Id, c.ContentType, SlugValue(c, slugField), data, c.CreatedAt, c.UpdatedAt, seo);
    }

    /*
     * Short cache window: long enough for a CDN to absorb bursts, short enough that a publish shows up
     * quickly. A publish-triggered rebuild (SSG) is the real freshness mechanism.
     */
    public static void SetCache(HttpContext http) =>
        http.Response.Headers.CacheControl = "public, max-age=60";
}

internal sealed class PublicListRequest : PaginatedRequest { }

/// <summary>GET /api/public/{type} — paged Published entries of a content type, masked and cacheable.</summary>
internal class ListPublishedEndpoint : Endpoint<PublicListRequest, PaginatedResponse<PublicContentResponse>>
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;

    public ListPublishedEndpoint(IQuerySession session, IConfiguration config)
    {
        _session = session;
        _config = config;
    }

    /// <summary>
    /// <c>Delivery:MaxRadiusKm</c>, the widest near filter a caller may ask for. Defaults to
    /// <see cref="DeliveryQuery.DefaultMaxRadiusKm"/>; a value that is not a positive number is
    /// treated as unset rather than as no limit.
    /// </summary>
    private double MaxRadiusKm()
    {
        var raw = _config["Delivery:MaxRadiusKm"];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : DeliveryQuery.DefaultMaxRadiusKm;
    }

    public override void Configure()
    {
        Get("/api/public/{type}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PublicListRequest req, CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (!PublicDelivery.IsDeliverable(def)) { await Send.NotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);

        /*
         * Parsed and refused before any SQL is built, and only against fields the type marks
         * Public. Filtering on a field the caller cannot read is an oracle: the value never appears
         * in a response, but which entries match reveals it.
         */
        // Each repeat is its own filter. StringValues.ToString() joins them with commas, which
        // turned ?filter[x][eq]=a&filter[x][eq]=b into one filter for the literal "a,b": it matches
        // nothing, and it lets a caller slip past MaxFilters by repeating a single key.
        var query = DeliveryQuery.Parse(
            HttpContext.Request.Query.SelectMany(kv =>
                kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v))),
            def,
            MaxRadiusKm());

        if (!query.IsValid)
        {
            AddError(query.Error!);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var (includes, includeError) = PublicDelivery.ParseIncludes(
            HttpContext.Request.Query["include"].FirstOrDefault(), def);
        if (includeError is not null)
        {
            AddError(includeError);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        /* Published + document-Public only; the DB filters the rest out. */
        var baseQuery = _session.Query<ContentDoc>()
            .Where(c => c.ContentType == type
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public);

        /*
         * Applied on top of the published/public predicate, never instead of it, so no filter can
         * widen what is visible. The integration test asserts that directly: a Draft matching the
         * filter must still not come back.
         */
        foreach (var f in query.Filters)
        {
            var (sql, parameters) = DeliveryQuery.ToSql(f);
            baseQuery = baseQuery.Where(c => c.MatchesSql(sql, parameters));
        }

        // Same chain, same rule: the proximity test narrows the published and public set and can
        // never replace it.
        if (query.Near is { } near)
        {
            var (sql, parameters) = DeliveryQuery.NearSql(near);
            baseQuery = baseQuery.Where(c => c.MatchesSql(sql, parameters));
        }

        var total = await baseQuery.CountAsync(ct);

        /*
         * A requested sort replaces the default rather than adding to it. CreatedAt stays as the
         * tiebreaker inside the fragment, so a page boundary cannot move between two entries that
         * compare equal. Without that, paging a list sorted on a field with duplicates can show the
         * same entry twice and skip another, which reads as data loss rather than as an ordering
         * question.
         */
        var ordered = query switch
        {
            { Near: { } n, DistanceSortDescending: { } desc } => baseQuery.OrderBySql(DeliveryQuery.DistanceOrderBySql(n, desc)),
            { Sort: { } sort } => baseQuery.OrderBySql(DeliveryQuery.ToOrderBySql(sort)),
            _ => baseQuery.OrderByDescending(c => c.CreatedAt),
        };

        var page = await ordered
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        var items = page
            .Select(c => PublicDelivery.ToPublic(c, def, slugField))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        // Read off the projected data, which the field has already survived: a near filter is only
        // accepted on a Public field, so the point is in there.
        if (query.Near is { } centre)
            items = items.Select(i => i with { DistanceKm = DeliveryQuery.DistanceKm(i.Data, centre) }).ToList();

        // Resolved after projection, never before. Projecting first means the reference id being
        // resolved has already survived the field allowlist, so a Sensitive reference field is not
        // resolvable by asking for it.
        items = await PublicDelivery.ResolveIncludesAsync(items, includes, def, _session, ct);

        PublicDelivery.SetCache(HttpContext);
        await Send.ResponseAsync(new PaginatedResponse<PublicContentResponse>
        {
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}

/// <summary>
/// Search results, deliberately not the paginated envelope every other collection uses.
/// </summary>
/// <remarks>
/// Decision recorded for #291, which asks that the exceptions be chosen rather than left as an
/// accident. This shape echoes the query back and reports how many of a bounded, ranked scan
/// matched. It is not a page of a larger set: there is no stable ordering to page through, no total
/// beyond the scan cap, and a caller asking for page 3 of a relevance ranking would get something
/// that changes under it. When this endpoint moves to Postgres full-text search, with a real total
/// and a stable order, it should take the envelope like everything else.
/// </remarks>
internal sealed record PublicSearchResponse(IReadOnlyList<PublicContentResponse> Results, int Count, string Query);

/// <summary>
/// GET /api/public/{type}/search?q=…&amp;limit=… — top public matches for a query. The literal "search"
/// segment wins over the {slug} route. Matching runs ONLY over allowlisted public fields (the entry is
/// projected to its public shape first), so a draft, a document-Sensitive entry, or a non-Public field
/// can never surface a result. A title/name hit outranks a body hit. Scans a bounded, recent window;
/// swap in Postgres full-text search for larger corpora.
/// </summary>
internal class PublicSearchEndpoint : EndpointWithoutRequest<PublicSearchResponse>
{
    private readonly IQuerySession _session;
    public PublicSearchEndpoint(IQuerySession session) => _session = session;

    private const int MaxResults = 50;
    private const int ScanCap = 1000;

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
        if (!PublicDelivery.IsDeliverable(def)) { await Send.NotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);

        if (q.Length < 2)
        {
            await Send.OkAsync(new PublicSearchResponse(Array.Empty<PublicContentResponse>(), 0, q), ct);
            return;
        }
        var candidates = await _session.Query<ContentDoc>()
                    .Where(c => c.ContentType == type
                                && c.Status == ContentStatus.Published
                                && c.Sensitivity == SensitivityLevel.Public
                                && c.SearchText != null
                                && c.SearchText.NgramSearch(q))
                    .OrderByNgramRank(c => c.SearchText!, q)
                    .Take(ScanCap)
                    .ToListAsync(ct);

        var results = candidates
            .Select(c => PublicDelivery.ToPublic(c, def, slugField)) /* project first: only public fields remain */
            .Where(r => r is not null).Select(r => r!)
            .Select(r => new { r, score = Score(r, q) })
            .OrderByDescending(x => x.score) // Title/field exact matches boost to the top; other n-gram matches follow
            .Take(limit)
            .Select(x => x.r)
            .ToList();

        PublicDelivery.SetCache(HttpContext);
        await Send.OkAsync(new PublicSearchResponse(results, results.Count, q), ct);
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
internal class GetBySlugEndpoint : EndpointWithoutRequest<PublicContentResponse>
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
        if (!PublicDelivery.IsDeliverable(def)) { await Send.NotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);
        if (slugField is null) { await Send.NotFoundAsync(ct); return; } /* not slug-addressable */

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
            /* The slug match runs in Postgres. This used to pull every published entry of the type
             * back and match in memory, so a blog with 20k posts deserialized 20k documents to
             * answer one request, and a 404 probe cost exactly the same. */
            var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(slugField, slug);
            match = await _session.Query<ContentDoc>()
                .Where(c => c.ContentType == type
                            && c.Status == ContentStatus.Published
                            && c.Sensitivity == SensitivityLevel.Public
                            && c.MatchesSql(sql, parameters))
                .FirstOrDefaultAsync(ct);
        }

        var projected = match is null ? null : PublicDelivery.ToPublic(match, def, slugField, allowUnpublished: previewId is not null);
        if (projected is null) { await Send.NotFoundAsync(ct); return; }

        if (previewId is not null)
            HttpContext.Response.Headers.CacheControl = "no-store"; /* never cache a draft */
        else
            PublicDelivery.SetCache(HttpContext);
        await Send.OkAsync(projected, ct);
    }
}
