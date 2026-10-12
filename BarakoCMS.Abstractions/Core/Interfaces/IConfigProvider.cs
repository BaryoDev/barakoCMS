using System.Text.Json;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// One section of a configuration document: the documents of one kind, exported in a stable shape
/// under stable keys, planned against what is stored, and applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys and shapes are contract.</b> A section is a JSON object from key to document. The key is
/// what identifies the document across deployments (a name or a slug, never a database id), and the
/// document is what <see cref="ExportAsync"/> writes and <see cref="PlanAsync"/> reads. A file
/// someone committed is read by a later version of the same provider, so within a major a key keeps
/// its meaning and a property does not disappear.
/// </para>
/// <para>
/// <b>A section present means "exactly these".</b> A stored document whose key the section does not
/// hold is planned as a destructive change, unless the provider documents that it never removes one.
/// A section left out of a file is not managed by it.
/// </para>
/// <para>
/// <b>The host runs the transaction.</b> Plan and apply run in one unit of work for the whole
/// document, section by section in <see cref="Order"/>, and each section is planned after the ones
/// before it were applied, so a section sees what an earlier one creates. A plan is the same run
/// rolled back. <see cref="ApplyAsync"/> stages into the scoped session it was resolved with and
/// does not commit. Register a provider as scoped.
/// </para>
/// <para>
/// <b>Secrets are never values.</b> An export names a credential at most, and an apply never stores
/// a credential it was given in a document.
/// </para>
/// </remarks>
public interface IConfigProvider
{
    /// <summary>The section's top-level key, for example <c>contentTypes</c>. Unique per host.</summary>
    string Section { get; }

    /// <summary>Where the section runs among the others. Lower runs first.</summary>
    int Order { get; }

    /// <summary>The capability a caller holds to export or plan this section.</summary>
    string ReadCapability { get; }

    /// <summary>The capability a caller holds to apply this section.</summary>
    string WriteCapability { get; }

    /// <summary>
    /// Whether an export that names no sections includes this one. False for a section that is
    /// content rather than configuration, which an export carries only when it is asked for by name.
    /// </summary>
    bool ExportsByDefault => true;

    /// <summary>
    /// Whether the host can apply this section as <see cref="ConfigCaller.System"/>, from a file it
    /// starts with. False for a section written for a signed-in caller, which a seed file skips.
    /// </summary>
    bool AppliesAsHost => true;

    /// <summary>
    /// The section's documents as they are stored, or null when the section has nothing to export.
    /// </summary>
    Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken cancellationToken);

    /// <summary>What applying <paramref name="desired"/> would change, refuse or need.</summary>
    Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken cancellationToken);

    /// <summary>Stages the changes of <paramref name="plan"/>.</summary>
    /// <returns>Why a change could not be applied after all. Any entry rolls the whole document back.</returns>
    Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken cancellationToken);
}
