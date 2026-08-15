using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.AI.Features;

public sealed record SemanticHit(string ContentType, string? Slug, string Title, double Score);
public sealed record SemanticResponse(IReadOnlyList<SemanticHit> Results, int Count, string Query);

/// <summary>
/// GET /api/public/{type}/semantic?q=…&amp;limit=… — vector search over a type's index. Embeds the query,
/// ranks stored vectors by cosine similarity, then re-verifies each candidate is STILL Published and
/// document-Public before returning it — so an entry unpublished or hidden since indexing never leaks.
/// Anonymous and cacheable; the literal "semantic" segment wins over the {slug} route.
/// </summary>
public class SemanticSearchEndpoint : EndpointWithoutRequest<SemanticResponse>
{
    private readonly IQuerySession _session;
    private readonly IEmbeddingClient _embed;

    public SemanticSearchEndpoint(IQuerySession session, IEmbeddingClient embed)
    {
        _session = session;
        _embed = embed;
    }

    private const int MaxResults = 20;
    private const double Floor = 0.4; // ignore weak matches so an unrelated query returns nothing

    public override void Configure()
    {
        Get("/api/public/{type}/semantic");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var q = (Query<string>("q", isRequired: false) ?? string.Empty).Trim();
        var limit = Math.Clamp(Query<int?>("limit", isRequired: false) ?? 5, 1, MaxResults);

        var empty = new SemanticResponse(Array.Empty<SemanticHit>(), 0, q);

        // Semantic search is public delivery in another form, so it answers to the same type-level
        // opt-in. Checked before anything else: embedding an unserviceable query would still spend a
        // model call, which makes an ungated endpoint a free compute endpoint as well as a leak.
        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (def is not { IsPubliclyDeliverable: true }) { await SendNotFoundAsync(ct); return; }

        if (q.Length < 2 || !_embed.IsConfigured) { await SendOkAsync(empty, ct); return; }

        var queryVector = await _embed.EmbedAsync(q, ct);
        if (queryVector is null) { await SendOkAsync(empty, ct); return; }

        var embeddings = await _session.Query<ContentEmbedding>()
            .Where(e => e.ContentType == type)
            .ToListAsync(ct);

        var ranked = embeddings
            .Select(e => (e, score: Vectors.Cosine(queryVector, e.Vector)))
            .Where(x => x.score >= Floor)
            .OrderByDescending(x => x.score)
            .Take(limit * 3) // buffer for the freshness filter below
            .ToList();

        var results = new List<SemanticHit>();
        foreach (var (e, score) in ranked)
        {
            // The vector is only a hint; the current content is the source of truth on visibility.
            var c = await _session.LoadAsync<Content>(e.Id, ct);
            if (c is null || c.Status != ContentStatus.Published || c.Sensitivity != SensitivityLevel.Public) continue;
            results.Add(new SemanticHit(type, e.Slug, e.Title, Math.Round(score, 4)));
            if (results.Count >= limit) break;
        }

        HttpContext.Response.Headers.CacheControl = "public, max-age=60";
        await SendOkAsync(new SemanticResponse(results, results.Count, q), ct);
    }
}
