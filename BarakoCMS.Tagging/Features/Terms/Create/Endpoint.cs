using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Marten.Exceptions;

namespace BarakoCMS.Tagging.Features.Terms.Create;

/// <summary>
/// POST /api/taxonomy/vocabularies/{vocabularyId}/terms. Adds a term, at the top or under another
/// term of the same vocabulary. A key is unique within its vocabulary.
/// </summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, TermResponse>
{
    public override void Configure()
    {
        Post("/api/taxonomy/vocabularies/{vocabularyId}/terms");
        Definition.RequireCapability(TaggingCapabilities.ManageTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (await session.LoadAsync<Vocabulary>(req.VocabularyId, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var tree = await TermTree.LoadAsync(session, req.VocabularyId, ct);
        if (tree.Count >= TaggingRules.MaxTermsPerVocabulary)
        {
            AddError($"A vocabulary holds at most {TaggingRules.MaxTermsPerVocabulary} terms.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (req.ParentId is { } parentId)
        {
            if (!tree.Contains(parentId))
            {
                AddError(r => r.ParentId!, "The parent is not a term of this vocabulary.");
                await Send.ErrorsAsync(400, ct);
                return;
            }

            if (tree.DepthOf(parentId) >= TaggingRules.MaxDepth)
            {
                AddError(r => r.ParentId!, $"Terms nest at most {TaggingRules.MaxDepth} levels deep.");
                await Send.ErrorsAsync(400, ct);
                return;
            }
        }

        var term = new Term
        {
            Id = TaggingIds.ForTerm(req.VocabularyId, req.Key),
            VocabularyId = req.VocabularyId,
            ParentId = req.ParentId,
            Key = req.Key,
            Name = req.Name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Insert(term);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.DocumentAlreadyExistsException)
        {
            AddError(r => r.Key, $"This vocabulary already has a term with the key '{req.Key}'.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.ResponseAsync(TermResponse.From(term), 201, ct);
    }
}
