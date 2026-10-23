using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Vocabularies.Update;

/// <summary>PUT /api/taxonomy/vocabularies/{id}. Renames a vocabulary. Its key stays.</summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, VocabularyResponse>
{
    public override void Configure()
    {
        Put("/api/taxonomy/vocabularies/{id}");
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

        vocabulary.Name = req.Name.Trim();
        vocabulary.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        session.Store(vocabulary);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(VocabularyResponse.From(vocabulary), ct);
    }
}
