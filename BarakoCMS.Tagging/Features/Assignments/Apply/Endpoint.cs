using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Assignments.Apply;

/// <summary>
/// POST /api/taxonomy/records/{contentType}/{recordId}/terms. Puts a term on a record. Applying a
/// term the record already carries changes nothing and answers the existing assignment.
/// </summary>
/// <remarks>
/// The record is not looked up. Its content type and id are only a name, which is what lets one
/// vocabulary tag records that live in different modules.
/// </remarks>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, AssignmentResponse>
{
    public override void Configure()
    {
        Post("/api/taxonomy/records/{contentType}/{recordId}/terms");
        Definition.RequireCapability(TaggingCapabilities.AssignTerms, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var term = await session.LoadAsync<Term>(req.TermId, ct);
        if (term is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var contentType = req.ContentType.Trim().ToLowerInvariant();
        var id = TaggingIds.ForAssignment(term.Id, contentType, req.RecordId);

        var assignment = await session.LoadAsync<TermAssignment>(id, ct);
        if (assignment is null)
        {
            assignment = new TermAssignment
            {
                Id = id,
                TermId = term.Id,
                VocabularyId = term.VocabularyId,
                ContentType = contentType,
                RecordId = req.RecordId,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = User.FindFirst("UserId")?.Value,
            };
            session.Store(assignment);
            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(AssignmentResponse.From(assignment), ct);
    }
}
