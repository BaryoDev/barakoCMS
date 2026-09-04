using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Submissions.List;

/// <summary>GET /api/forms/{name}/submissions, newest first, optionally within ?from=&amp;to=.</summary>
public class Endpoint : Endpoint<SubmissionsRequest, PaginatedResponse<SubmissionResponse>>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/forms/{name}/submissions");
        Definition.RequireCapability(FormsCapabilities.ViewFormSubmissions, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(SubmissionsRequest req, CancellationToken ct)
    {
        var exists = await _session.Query<FormDefinition>().AnyAsync(f => f.Name == req.Name, ct);
        if (!exists) { await Send.NotFoundAsync(ct); return; }

        var query = SubmissionQuery.For(_session, req.Name, req.From, req.To);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.SubmittedAt)
            .Skip(req.Skip).Take(req.Take)
            .ToListAsync(ct);

        await Send.ResponseAsync(new PaginatedResponse<SubmissionResponse>
        {
            Items = items.Select(SubmissionResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}

internal static class SubmissionQuery
{
    public static IQueryable<FormSubmission> For(IQuerySession session, string name, DateTime? from, DateTime? to)
    {
        var query = session.Query<FormSubmission>().Where(s => s.FormName == name);
        if (from is { } f) query = query.Where(s => s.SubmittedAt >= f);
        if (to is { } t) query = query.Where(s => s.SubmittedAt <= t);
        return query;
    }
}
