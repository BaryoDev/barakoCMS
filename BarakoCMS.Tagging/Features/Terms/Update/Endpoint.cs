using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Terms.Update;

/// <summary>
/// PUT /api/taxonomy/terms/{id}. Renames a term and sets where it sits. The key stays. A term
/// cannot move under itself or under one of its own descendants.
/// </summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, TermResponse>
{
    public override void Configure()
    {
        Put("/api/taxonomy/terms/{id}");
        Definition.RequireCapability(TaggingCapabilities.ManageTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var term = await session.LoadAsync<Term>(req.Id, ct);
        if (term is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.ParentId != term.ParentId && req.ParentId is { } parentId)
        {
            await TaggingLocks.ForVocabularyAsync(session, term.VocabularyId, ct);
            var tree = await TermTree.LoadAsync(session, term.VocabularyId, ct);
            if (!tree.Contains(parentId))
            {
                AddError(r => r.ParentId!, "The parent is not a term of this vocabulary.");
                await Send.ErrorsAsync(400, ct);
                return;
            }

            if (tree.SelfAndDescendants(term.Id).Contains(parentId))
            {
                AddError(r => r.ParentId!, "A term cannot sit under itself or under one of its own descendants.");
                await Send.ErrorsAsync(400, ct);
                return;
            }

            if (tree.DepthOf(parentId) + tree.HeightOf(term.Id) > TaggingRules.MaxDepth)
            {
                AddError(r => r.ParentId!, $"Terms nest at most {TaggingRules.MaxDepth} levels deep.");
                await Send.ErrorsAsync(400, ct);
                return;
            }
        }

        term.Name = req.Name.Trim();
        term.ParentId = req.ParentId;
        session.Store(term);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(TermResponse.From(term), ct);
    }
}
