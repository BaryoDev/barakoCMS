using barakoCMS.Models;

namespace barakoCMS.Features.Modules.List;

/// <summary>
/// Defaults to the largest page, like every other administrative list. A deployment runs a handful
/// of modules, so a caller asking nothing should get all of them.
/// </summary>
/// <remarks>
/// Inheriting the shared list request brings a <c>sortOrder</c> the handler does not read, so the
/// generated client advertises a parameter that changes nothing. Matching the other list endpoints
/// beats a bespoke request type here, and the fixed order is documented rather than left to be
/// discovered: see docs/module-inventory.md.
/// </remarks>
internal sealed class ListModulesRequest : ListRequest;

/// <summary>
/// What core will say about a module: the name it registered under, the module contract version it
/// declared, and whether the enabled list let it run. Nothing else is safe to publish here, because
/// everything else a module knows is about the deployment rather than about the module.
/// </summary>
/// <param name="Name"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
/// <param name="ContractVersion">
/// <see cref="barakoCMS.Modules.IBarakoModule.ContractVersion"/>. Zero means the module did not
/// state one, which the contract accepts, so zero is an answer rather than a missing value.
/// </param>
/// <param name="Enabled">
/// Whether the module runs in this process. False means it was added or discovered and then left
/// out by <c>BarakoCMS:Modules:Enabled</c>; a module that is not installed at all is not listed.
/// </param>
internal sealed record ModuleSummary(string Name, int ContractVersion, bool Enabled);
