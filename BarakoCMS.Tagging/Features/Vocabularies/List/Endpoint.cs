using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Vocabularies.List;

/// <summary>GET /api/taxonomy/vocabularies. The tenant's vocabularies, paged, by key.</summary>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<VocabularyResponse>>
{
    public override void Configure()
    {
        Get("/api/taxonomy/vocabularies");
        Definition.RequireCapability(TaggingCapabilities.ViewTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var page = await session.Query<Vocabulary>().OrderBy(v => v.Key).ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<VocabularyResponse>
        {
            Items = page.Items.Select(VocabularyResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
