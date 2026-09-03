using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;

namespace barakoCMS.Features.Modules.List;

/// <summary>
/// GET /api/modules, reporting which modules this instance has registered.
/// </summary>
/// <remarks>
/// Read straight off the container. <c>AddBarakoCMS</c> registers each opted-in module as a
/// singleton <see cref="IBarakoModule"/>, so what the container holds is what the host actually
/// booted with, not a list somebody maintains alongside it.
///
/// Gated on <c>Roles("SuperAdmin", "Admin")</c> rather than on a capability. Every name in
/// <see cref="SystemCapabilities"/> covers a management surface this endpoint neither reads nor
/// writes, and the nearest neighbour by purpose, <c>Features/Monitoring</c>, still gates the same
/// way. See issue #185 for the whole argument.
/// </remarks>
internal sealed class Endpoint : Endpoint<ListModulesRequest, PaginatedResponse<ModuleSummary>>
{
    private readonly IEnumerable<IBarakoModule> _modules;

    public Endpoint(IEnumerable<IBarakoModule> modules) => _modules = modules;

    public override void Configure()
    {
        Get("/api/modules");
        Definition.RequireCapability(SystemCapabilities.ViewModules, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListModulesRequest req, CancellationToken ct)
    {
        // Ordered by name so two calls, and two deployments of the same set, agree. Registration
        // order is meaningful to the host (it decides who configures first) and meaningless here.
        IReadOnlyList<ModuleSummary> modules = _modules
            .Select(m => new ModuleSummary(m.Name, m.ContractVersion))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        await Send.OkAsync(modules.ToPagedResponse(req), ct);
    }
}
