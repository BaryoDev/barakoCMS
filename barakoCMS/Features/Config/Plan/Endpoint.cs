using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Config.Plan;

/// <summary>
/// POST /api/config/plan: what applying a document to the caller's tenant would change, refuse or
/// need. Writes nothing.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(ConfigEngine engine) : Endpoint<Request, ConfigPlanResponse>
{
    public override void Configure()
    {
        Post("/api/config/plan");
        Definition.RequireCapability(SystemCapabilities.ManageConfig, CapabilityGate.AdminLegacyRoles);
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var result = await engine.RunAsync(
            req.Document, ConfigCaller.ForUser(User), apply: false, allowDestructive: false, ct);
        await Send.ResponseAsync(result.Response, result.StatusCode, ct);
    }
}
