using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Terms.List;

/// <summary>GET /api/taxonomy/vocabularies/{vocabularyId}/terms. A vocabulary's terms, paged, by key.</summary>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<TermResponse>>
{
    public override void Configure()
    {
        Get("/api/taxonomy/vocabularies/{vocabularyId}/terms");
        Definition.RequireCapability(TaggingCapabilities.ViewTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (await session.LoadAsync<Vocabulary>(req.VocabularyId, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var terms = session.Query<Term>().Where(t => t.VocabularyId == req.VocabularyId);
        if (req.ParentId is { } parentId)
        {
            terms = terms.Where(t => t.ParentId == parentId);
        }

        var page = await terms.OrderBy(t => t.Key).ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<TermResponse>
        {
            Items = page.Items.Select(TermResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
