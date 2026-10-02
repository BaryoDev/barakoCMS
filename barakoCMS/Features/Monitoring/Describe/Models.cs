using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.Monitoring.Describe;

internal sealed class DescribeResponse
{
    // The contract the names below were read under, the same number GET /api/meta and the
    // X-Api-Contract-Version header report.
    public int ApiContractVersion { get; set; }

    public IReadOnlyList<DescribedFieldType> FieldTypes { get; set; } = [];

    public IReadOnlyList<DescribedRule> Rules { get; set; } = [];

    // Each of the three below is null for a caller who would be refused by the endpoint that
    // already lists it, and a list, possibly empty, for one who would not. Null is "not yours to
    // see" (or, for workflowActions, that the registry could not be read), empty is "none", and a
    // console has to be able to tell them apart.

    /// <summary>What <c>GET /api/capabilities</c> lists, to a caller who may read that.</summary>
    public IReadOnlyList<KnownCapability>? Capabilities { get; set; }

    /// <summary>What <c>GET /api/workflows/actions</c> lists, to a caller who may read that.</summary>
    public IReadOnlyList<WorkflowActionMetadata>? WorkflowActions { get; set; }

    /// <summary>The modules that run, to a caller who may read <c>GET /api/modules</c>.</summary>
    public IReadOnlyList<DescribedModule>? Modules { get; set; }
}

/// <param name="Name">The canonical name, as a content type's field declares it.</param>
/// <param name="Aliases">Other names accepted for the same type.</param>
/// <param name="EditorHint">The input control a console should offer for it.</param>
/// <param name="RuleNames">
/// The validation rules a field of this type may declare, each the <c>name</c> of an entry in the
/// document's <c>rules</c>.
/// </param>
internal sealed record DescribedFieldType(
    string Name,
    IReadOnlyList<string> Aliases,
    string EditorHint,
    IReadOnlyList<string> RuleNames);

/// <param name="Name">The key under a field's <c>validationRules</c>.</param>
/// <param name="Aliases">Other keys read as the same rule.</param>
internal sealed record DescribedRule(string Name, IReadOnlyList<string> Aliases);

/// <param name="Name"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
internal sealed record DescribedModule(string Name);
