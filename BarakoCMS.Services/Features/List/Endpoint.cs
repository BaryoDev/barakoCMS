using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Services.Features.List;

/// <summary>GET /api/services: the registered services with their health, by name.</summary>
internal sealed class ListServicesEndpoint(IQuerySession session, IConfiguration configuration)
    : Endpoint<ListRequest, PaginatedResponse<ServiceDto>>
{
    public override void Configure()
    {
        Get("/api/services");
        Definition.RequireCapability(
            ServicesCapabilities.ManageServices, ServicesCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        if (!await ServiceAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        var page = await session.Query<ServiceRegistration>()
            .OrderBy(s => s.Name)
            .ToPagedResponseAsync(req, ct);

        var now = DateTime.UtcNow;
        await Send.OkAsync(new PaginatedResponse<ServiceDto>
        {
            Items = page.Items.Select(s => ServiceDto.From(s, now)).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
