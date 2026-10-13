using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Config.Export;

/// <summary>
/// GET /api/config/export: the caller's tenant's configuration as one document, in the shape
/// <c>POST /api/config/apply</c> takes back.
/// </summary>
/// <remarks>
/// Gated, because configuration says a great deal about a deployment: where its connectors point
/// and who may do what. Each section is also gated on its own read capability, and a section the
/// caller may not read is named under <c>withheld</c> rather than exported.
/// </remarks>
internal sealed class Endpoint(ConfigEngine engine) : Endpoint<Request, ConfigExportResponse>
{
    public override void Configure()
    {
        Get("/api/config/export");
        Definition.RequireCapability(SystemCapabilities.ManageConfig, CapabilityGate.AdminLegacyRoles);
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct) =>
        await Send.OkAsync(await engine.ExportAsync(ConfigCaller.ForUser(User), req.Named(), ct), ct);
}
