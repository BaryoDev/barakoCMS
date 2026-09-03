using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.AI.Features;

public sealed record IndexResponse(string Type, int Indexed, int Skipped);

/// <summary>
/// POST /api/ai/index/{type} — (re)build the vector index for a content type in the current tenant.
/// Embeds each Published, document-Public entry from its Public fields only. Admin-only, since it drives
/// the embedding backend. Returns how many were indexed vs skipped (skips mean the backend was
/// unreachable for that item).
/// </summary>
public class IndexEndpoint : EndpointWithoutRequest<IndexResponse>
{
    private readonly IDocumentSession _session;
    private readonly IEmbeddingClient _embed;

    public IndexEndpoint(IDocumentSession session, IEmbeddingClient embed)
    {
        _session = session;
        _embed = embed;
    }

    private const int Cap = 500;

    public override void Configure()
    {
        Post("/api/ai/index/{type}");
        Definition.RequireCapability(
            AiCapabilities.ManageSearchIndex, AiCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;

        if (!_embed.IsConfigured)
        {
            await Send.ResponseAsync(new IndexResponse(type, 0, 0), 503, ct);
            return;
        }

        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (def is null) { await Send.NotFoundAsync(ct); return; }

        var items = await _session.Query<Content>()
            .Where(c => c.ContentType == type
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .Take(Cap)
            .ToListAsync(ct);

        int indexed = 0, skipped = 0;
        foreach (var c in items)
        {
            var vector = await _embed.EmbedAsync(PublicText.ToEmbeddableText(c, def), ct);
            if (vector is null) { skipped++; continue; }

            _session.Store(new ContentEmbedding
            {
                Id = c.Id,
                ContentType = type,
                Slug = PublicText.SlugValue(c, def),
                Title = PublicText.TitleOf(c, def),
                Vector = vector,
                UpdatedAt = DateTime.UtcNow,
            });
            indexed++;
        }

        await _session.SaveChangesAsync(ct);
        await Send.OkAsync(new IndexResponse(type, indexed, skipped), ct);
    }
}
