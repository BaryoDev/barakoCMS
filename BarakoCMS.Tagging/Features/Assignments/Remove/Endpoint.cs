using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Assignments.Remove;

/// <summary>DELETE /api/taxonomy/records/{contentType}/{recordId}/terms/{termId}. Takes a term off a record.</summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/taxonomy/records/{contentType}/{recordId}/terms/{termId}");
        Definition.RequireCapability(TaggingCapabilities.AssignTerms, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var id = TaggingIds.ForAssignment(req.TermId, TaggingIds.TypeKey(req.ContentType), TaggingIds.RecordKey(req.RecordId));

        var assignment = await session.LoadAsync<TermAssignment>(id, ct);
        if (assignment is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete(assignment);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
