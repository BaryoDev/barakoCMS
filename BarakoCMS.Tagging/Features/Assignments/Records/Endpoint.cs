using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Assignments.Records;

/// <summary>
/// GET /api/taxonomy/terms/{termId}/records. The records carrying a term, across content types
/// unless one is named, paged, newest first. With <c>includeDescendants</c>, a record carrying a
/// term nested under this one counts too.
/// </summary>
internal sealed class Endpoint(IQuerySession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<Request, PaginatedResponse<AssignmentResponse>>
{
    public override void Configure()
    {
        Get("/api/taxonomy/terms/{termId}/records");
        Definition.RequireCapability(TaggingCapabilities.ViewTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var access = await RecordReadAccess.ForCallerAsync(session, permissions, Resolve<barakoCMS.Infrastructure.Services.IBranchScopeGuard>(), User, ct);
        if (access is null
            || (!string.IsNullOrWhiteSpace(req.ContentType) && !await access.CanReadTypeAsync(TaggingIds.TypeKey(req.ContentType), ct)))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var term = await session.LoadAsync<Term>(req.TermId, ct);
        if (term is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var termIds = req.IncludeDescendants
            ? (await TermTree.LoadAsync(session, term.VocabularyId, ct)).SelfAndDescendants(term.Id).ToArray()
            : [term.Id];

        var assignments = session.Query<TermAssignment>().Where(a => termIds.Contains(a.TermId));
        if (!string.IsNullOrWhiteSpace(req.ContentType))
        {
            var contentType = TaggingIds.TypeKey(req.ContentType);
            assignments = assignments.Where(a => a.ContentType == contentType);
        }

        // Read access is per record, so it is applied before paging: a page cut first would come back
        // short and a total taken first would count records the caller cannot see. The newest
        // MaxScanned assignments are the ones considered.
        var candidates = await assignments
            .OrderByDescending(a => a.AssignedAt)
            .ThenBy(a => a.Id)
            .Take(MaxScanned)
            .ToListAsync(ct);
        var readable = await access.ReadableAsync(candidates, ct);

        await Send.OkAsync(new PaginatedResponse<AssignmentResponse>
        {
            Items = readable.Skip(req.Skip).Take(req.Take).Select(AssignmentResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = readable.Count,
        }, ct);
    }

    private const int MaxScanned = 5000;
}
