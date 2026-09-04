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
/// declared, whether the enabled list let it run, and what the schema preflight found for it.
/// Nothing else is safe to publish here, because everything else a module knows is about the
/// deployment rather than about the module.
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
/// <param name="SchemaState">
/// One of <see cref="barakoCMS.Modules.ModuleSchemaState"/>: <c>ready</c> when the preflight found
/// nothing the store would refuse, <c>needs-migration</c> when the module wanted a change to an
/// existing object, <c>unknown</c> when the preflight did not run for it (switched off, or the
/// module is not enabled).
/// </param>
/// <param name="SchemaChanges">
/// The existing database objects the module wanted to change, by qualified name. Empty unless
/// <paramref name="SchemaState"/> is <c>needs-migration</c>.
/// </param>
internal sealed record ModuleSummary(
    string Name,
    int ContractVersion,
    bool Enabled,
    string SchemaState,
    IReadOnlyList<string> SchemaChanges);
