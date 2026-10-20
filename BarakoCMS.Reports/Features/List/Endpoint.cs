using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Reports.Features.List;

/// <summary>GET /api/reports. The tenant's saved reports, paged.</summary>
internal sealed class Endpoint(IQuerySession session) : Endpoint<PaginatedRequest, PaginatedResponse<Create.Response>>
{
    public override void Configure()
    {
        Get("/api/reports");
        Definition.RequireCapability(ReportsCapabilities.ManageReports, ReportsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var page = await session.Query<ReportDefinition>()
            .OrderBy(r => r.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<Create.Response>
        {
            Items = page.Items.Select(Create.Response.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}
