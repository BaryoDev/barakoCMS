using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Services.Features.Heartbeat;

internal sealed class HeartbeatRequest
{
    public string Name { get; set; } = string.Empty;
}

internal sealed record HeartbeatResponse(string Name, string Health, DateTime LastHeartbeatAt);

/// <summary>
/// POST /api/services/{name}/heartbeat: the service says it is alive. Held by send_service_heartbeat
/// alone, so the identity a service reports with cannot edit the registry. Never replayed from a
/// stored response, because a replay would answer 200 without recording the beat.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class HeartbeatEndpoint(IDocumentSession session) : Endpoint<HeartbeatRequest, HeartbeatResponse>
{
    public override void Configure()
    {
        Post("/api/services/{name}/heartbeat");
        Definition.RequireCapability(
            ServicesCapabilities.SendServiceHeartbeat, ServicesCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(HeartbeatRequest req, CancellationToken ct)
    {
        var name = ServiceAdmin.Normalize(req.Name);
        var service = await session.Query<ServiceRegistration>().FirstOrDefaultAsync(s => s.Name == name, ct);
        if (service is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = DateTime.UtcNow;
        service.LastHeartbeatAt = now;
        service.UpdatedAt = now;
        session.Store(service);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new HeartbeatResponse(service.Name, ServiceHealth.Evaluate(service, now).ToString(), now), ct);
    }
}
