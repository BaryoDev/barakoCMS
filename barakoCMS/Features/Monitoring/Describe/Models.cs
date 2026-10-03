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

    /// <summary>Every value a field's <c>editor</c> may hold, and the field types each is for.</summary>
    public IReadOnlyList<DescribedFieldHint> FieldEditors { get; set; } = [];

    /// <summary>Every value a field's <c>role</c> may hold, and the field types each is for.</summary>
    public IReadOnlyList<DescribedFieldHint> FieldRoles { get; set; } = [];

    /// <summary>What a content type's uniqueness rule may compare, and how many a type may hold.</summary>
    public DescribedUniqueness Uniqueness { get; set; } = DescribedUniqueness.Current;

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

/// <param name="Name">The value, as a field definition carries it.</param>
/// <param name="FieldTypes">
/// The field types it may be declared on, each the <c>name</c> of an entry in the document's
/// <c>fieldTypes</c>.
/// </param>
internal sealed record DescribedFieldHint(string Name, IReadOnlyList<string> FieldTypes);

/// <param name="FieldTypes">The field types a rule may name, each the <c>name</c> of an entry in <c>fieldTypes</c>.</param>
/// <param name="CreatorField">The name that stands for the entry's creator in a rule's fields.</param>
/// <param name="MaxRules">The most rules one type may declare.</param>
/// <param name="MaxFields">The most fields one rule may compare.</param>
internal sealed record DescribedUniqueness(
    IReadOnlyList<string> FieldTypes,
    string CreatorField,
    int MaxRules,
    int MaxFields)
{
    public static DescribedUniqueness Current { get; } = new(
        barakoCMS.Core.Validation.UniquenessRules.FieldTypes,
        UniquenessRule.CreatedByField,
        barakoCMS.Core.Validation.UniquenessRules.MaxRules,
        barakoCMS.Core.Validation.UniquenessRules.MaxFields);
}

/// <param name="Name"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
/// <param name="HttpContractVersion">
/// <see cref="barakoCMS.Modules.IBarakoModule.HttpContractVersion"/>, the version of the module's
/// own endpoints. Zero means the module states none. Not the <c>contractVersion</c> of
/// <c>GET /api/modules</c>, which is what the module was compiled against.
/// </param>
internal sealed record DescribedModule(string Name, int HttpContractVersion);
