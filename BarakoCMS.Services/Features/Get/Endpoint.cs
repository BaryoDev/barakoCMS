using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Services.Features.Get;

internal sealed class GetServiceRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>GET /api/services/{name}: one service with its health.</summary>
internal sealed class GetServiceEndpoint(IQuerySession session, IConfiguration configuration) : Endpoint<GetServiceRequest, ServiceDto>
{
    public override void Configure()
    {
        Get("/api/services/{name}");
        Definition.RequireCapability(
            ServicesCapabilities.ManageServices, ServicesCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(GetServiceRequest req, CancellationToken ct)
    {
        if (!await ServiceAdmin.AllowedAsync(session, User, configuration, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        var name = ServiceAdmin.Normalize(req.Name);
        var service = await session.Query<ServiceRegistration>().FirstOrDefaultAsync(s => s.Name == name, ct);
        if (service is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ServiceDto.From(service, DateTime.UtcNow), ct);
    }
}
