using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Terms.Delete;

/// <summary>
/// DELETE /api/taxonomy/terms/{id}. Removes a term and the assignments that point at it. A term
/// with children is refused with 409, so a branch is never removed by accident.
/// </summary>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/taxonomy/terms/{id}");
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

        if (await session.Query<Term>().AnyAsync(t => t.ParentId == term.Id, ct))
        {
            AddError("The term has children. Move or delete them first.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        session.DeleteWhere<TermAssignment>(a => a.TermId == term.Id);
        session.Delete(term);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
