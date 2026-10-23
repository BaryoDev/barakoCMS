using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Tagging;

/// <summary>What this module's endpoints ask for instead of a role name.</summary>
public static class TaggingCapabilities
{
    /// <summary>Read vocabularies and terms, the terms on a record and the records on a term.</summary>
    public const string ViewTaxonomy = "view_taxonomy";

    /// <summary>Create, rename and delete vocabularies and terms.</summary>
    public const string ManageTaxonomy = "manage_taxonomy";

    /// <summary>Put a term on a record, or take it off.</summary>
    public const string AssignTerms = "assign_terms";

    internal static readonly string[] All = [ViewTaxonomy, ManageTaxonomy, AssignTerms];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>Roles that pass the gate by name while the legacy role fallback is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
