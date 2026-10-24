using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Tagging.Features.Assignments.ForRecord;

/// <summary>GET /api/taxonomy/records/{contentType}/{recordId}/terms. The terms on one record, paged, newest first.</summary>
internal sealed class Endpoint(IQuerySession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<Request, PaginatedResponse<TermResponse>>
{
    public override void Configure()
    {
        Get("/api/taxonomy/records/{contentType}/{recordId}/terms");
        Definition.RequireCapability(TaggingCapabilities.ViewTaxonomy, TaggingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var contentType = req.ContentType.Trim().ToLowerInvariant();

        var access = await RecordReadAccess.ForCallerAsync(session, permissions, User, ct);
        if (access is null || !await access.CanReadAsync(contentType, req.RecordId, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var page = await session.Query<TermAssignment>()
            .Where(a => a.ContentType == contentType && a.RecordId == req.RecordId)
            .OrderByDescending(a => a.AssignedAt)
            .ThenBy(a => a.Id)
            .ToPagedResponseAsync(req, ct);

        var ids = page.Items.Select(a => a.TermId).ToArray();
        var terms = ids.Length == 0
            ? []
            : (await session.Query<Term>().Where(t => ids.Contains(t.Id)).ToListAsync(ct)).ToDictionary(t => t.Id);

        await Send.OkAsync(new PaginatedResponse<TermResponse>
        {
            Items = page.Items.Where(a => terms.ContainsKey(a.TermId)).Select(a => TermResponse.From(terms[a.TermId])).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
