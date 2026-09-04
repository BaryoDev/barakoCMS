using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Definitions.List;

/// <summary>GET /api/forms. The tenant's forms, by name.</summary>
public class Endpoint : Endpoint<ListRequest, PaginatedResponse<FormResponse>>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/forms");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var query = _session.Query<FormDefinition>();
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(f => f.Name)
            .Skip(req.Skip).Take(req.Take)
            .ToListAsync(ct);

        await Send.ResponseAsync(new PaginatedResponse<FormResponse>
        {
            Items = items.Select(FormResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}
