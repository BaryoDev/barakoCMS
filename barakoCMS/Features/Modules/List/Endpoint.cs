using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;

namespace barakoCMS.Features.Modules.List;

/// <summary>
/// GET /api/modules, reporting every module this instance saw, whether it runs, and what the
/// schema preflight found for it.
/// </summary>
/// <remarks>
/// Read from the <see cref="ModuleCatalogue"/> <c>AddBarakoCMS</c> built, which holds what the host
/// added or discovery found, not a list somebody maintains alongside it. The catalogue rather than
/// the <see cref="IBarakoModule"/> singletons, because those hold only the modules the enabled list
/// let run, and a module switched off by configuration is still installed.
///
/// Gated on <c>Roles("SuperAdmin", "Admin")</c> rather than on a capability. Every name in
/// <see cref="SystemCapabilities"/> covers a management surface this endpoint neither reads nor
/// writes. See issue #185 for the whole argument, and
/// <c>RoleGateTests.The_core_routes_still_on_a_role_name_are_the_two_that_are_meant_to_be</c> for the
/// pinned list this belongs to now that #443 has migrated everything else.
/// </remarks>
internal sealed class Endpoint : Endpoint<ListModulesRequest, PaginatedResponse<ModuleSummary>>
{
    private readonly ModuleCatalogue _catalogue;
    private readonly ModuleSchemaReport _schema;

    public Endpoint(ModuleCatalogue catalogue, ModuleSchemaReport schema)
    {
        _catalogue = catalogue;
        _schema = schema;
    }

    public override void Configure()
    {
        Get("/api/modules");
        Definition.RequireCapability(SystemCapabilities.ViewModules, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListModulesRequest req, CancellationToken ct)
    {
        // Ordered by name so two calls, and two deployments of the same set, agree. Registration
        // order is meaningful to the host (it decides who configures first) and meaningless here.
        IReadOnlyList<ModuleSummary> modules = _catalogue.Entries
            .Select(Summarise)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        await Send.OkAsync(modules.ToPagedResponse(req), ct);
    }

    /// <summary>
    /// Unknown, not ready, whenever the preflight has nothing on the module: the check was switched
    /// off, or the module is not enabled and so registered no schema for it to look at.
    /// </summary>
    private ModuleSummary Summarise(ModuleCatalogueEntry entry)
    {
        var finding = entry.Enabled ? _schema.For(entry.Name) : null;
        return finding is null
            ? new ModuleSummary(entry.Name, entry.ContractVersion, entry.Enabled, ModuleSchemaState.Unknown, [])
            : new ModuleSummary(entry.Name, entry.ContractVersion, entry.Enabled, finding.State,
                finding.Changes.Select(c => c.Name).ToArray());
    }
}
