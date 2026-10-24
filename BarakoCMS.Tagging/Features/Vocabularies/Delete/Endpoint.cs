using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Vocabularies.Delete;

/// <summary>DELETE /api/taxonomy/vocabularies/{id}. Removes the vocabulary, its terms and their assignments.</summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/taxonomy/vocabularies/{id}");
        Definition.RequireCapability(TaggingCapabilities.ManageTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var vocabulary = await session.LoadAsync<Vocabulary>(req.Id, ct);
        if (vocabulary is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.DeleteWhere<TermAssignment>(a => a.VocabularyId == vocabulary.Id);
        session.DeleteWhere<Term>(t => t.VocabularyId == vocabulary.Id);
        session.Delete(vocabulary);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
