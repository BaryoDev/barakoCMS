using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Marten.Exceptions;

namespace BarakoCMS.Tagging.Features.Vocabularies.Create;

/// <summary>POST /api/taxonomy/vocabularies. Creates a vocabulary; its key is unique within the tenant.</summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, VocabularyResponse>
{
    public override void Configure()
    {
        Post("/api/taxonomy/vocabularies");
        Definition.RequireCapability(TaggingCapabilities.ManageTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var vocabulary = new Vocabulary
        {
            Id = TaggingIds.ForVocabulary(req.Key),
            Key = req.Key,
            Name = req.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Insert(vocabulary);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.DocumentAlreadyExistsException)
        {
            AddError(r => r.Key, $"A vocabulary with the key '{req.Key}' already exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.ResponseAsync(VocabularyResponse.From(vocabulary), 201, ct);
    }
}
