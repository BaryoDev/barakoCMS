using JasperFx;

namespace barakoCMS.Models;

/// <summary>
/// Records that one <c>UpdateField</c> workflow attempt already applied its change, so a reclaimed
/// attempt that reruns after its outcome was discarded finds its own mark and writes nothing a
/// second time.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a key on the content's own <c>Data</c>. Two things ruled that out. First,
/// <c>Data</c> is replaced wholesale by every content edit (<c>Content.Apply(ContentUpdated, ...)</c>
/// and <c>Features/Content/Update/Endpoint.cs</c>), so an editor saving the entry between the apply
/// and a reclaimed retry silently dropped the mark, and the very race this exists to close reopened.
/// Second, nothing filters an unknown <c>Data</c> key out of an authenticated read or an export
/// (<c>SensitivityService</c> only masks fields a schema declares, and both
/// <c>Features/Content/Get/Endpoint.cs</c> and <c>BarakoCMS.Portability.ExportEndpoint</c> copy
/// <c>Data</c> whole), so the mark was visible to anyone who could read the entry.
/// </para>
/// <para>
/// A document of its own does not have either problem, and does not lose the one thing that made
/// <c>Data</c> tempting: <see cref="barakoCMS.Features.Workflows.Actions.UpdateFieldAction"/> stores
/// this through the same <c>IDocumentSession</c> as the content write, and Marten commits a session
/// as one transaction across document types, so the two still land together or not at all.
/// </para>
/// </remarks>
public class WorkflowFieldApplyMarker
{
    /// <summary>
    /// The action's <c>IdempotencyKey</c>: stable across every rerun of one attempt, since it is
    /// derived from the run id and the action's position in it, and unique enough on its own that
    /// no target content id needs folding in.
    /// </summary>
    [Identity]
    public string Key { get; set; } = string.Empty;

    /// <summary>The last attempt number that applied its change under this key.</summary>
    public int Attempt { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
