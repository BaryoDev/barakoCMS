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
        var access = await RecordReadAccess.ForCallerAsync(session, permissions, User, ct);
        if (access is null
            || (!string.IsNullOrWhiteSpace(req.ContentType) && !await access.CanReadTypeAsync(req.ContentType.Trim().ToLowerInvariant(), ct)))
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
            var contentType = req.ContentType.Trim().ToLowerInvariant();
            assignments = assignments.Where(a => a.ContentType == contentType);
        }

        var page = await assignments
            .OrderByDescending(a => a.AssignedAt)
            .ThenBy(a => a.Id)
            .ToPagedResponseAsync(req, ct);

        var readable = new List<TermAssignment>();
        foreach (var assignment in page.Items)
        {
            if (await access.CanReadAsync(assignment.ContentType, assignment.RecordId, ct))
                readable.Add(assignment);
        }

        await Send.OkAsync(new PaginatedResponse<AssignmentResponse>
        {
            Items = readable.Select(AssignmentResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
