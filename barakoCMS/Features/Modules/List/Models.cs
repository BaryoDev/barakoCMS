using barakoCMS.Models;

namespace barakoCMS.Features.Modules.List;

/// <summary>
/// Defaults to the largest page, like every other administrative list. A deployment runs a handful
/// of modules, so a caller asking nothing should get all of them.
/// </summary>
internal sealed class ListModulesRequest : ListRequest;

/// <summary>
/// What core will say about a module: the name it registered under and the module contract version
/// it declared. Nothing else is safe to publish here, because everything else a module knows is
/// about the deployment rather than about the module.
/// </summary>
/// <param name="Name"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
/// <param name="ContractVersion">
/// <see cref="barakoCMS.Modules.IBarakoModule.ContractVersion"/>. Zero means the module did not
/// state one, which the contract accepts, so zero is an answer rather than a missing value.
/// </param>
internal sealed record ModuleSummary(string Name, int ContractVersion);
