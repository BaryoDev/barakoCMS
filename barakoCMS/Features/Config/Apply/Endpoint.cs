using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Config.Apply;

/// <summary>
/// POST /api/config/apply: converges the caller's tenant to a document, all of it or none of it.
/// </summary>
/// <remarks>
/// Answers with the plan it ran. 400 when a section cannot be applied as written, 403 when the
/// caller may not make a change it asks for, and 409 when it holds a destructive change and
/// <c>allowDestructive</c> was not sent. In each of those nothing is written.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(ConfigEngine engine) : Endpoint<Request, ConfigPlanResponse>
{
    public override void Configure()
    {
        Post("/api/config/apply");
        Definition.RequireCapability(SystemCapabilities.ManageConfig, CapabilityGate.AdminLegacyRoles);
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var result = await engine.RunAsync(
            req.Document, ConfigCaller.ForUser(User), apply: true, req.AllowDestructive, ct);
        await Send.ResponseAsync(result.Response, result.StatusCode, ct);
    }
}
