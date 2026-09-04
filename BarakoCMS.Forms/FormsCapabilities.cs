namespace BarakoCMS.Forms;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. A name a module declares is grantable the day the module ships: its endpoints put
/// it on the routing table, which is where <c>GET /api/capabilities</c> reads from. See issue #443.
/// </remarks>
public static class FormsCapabilities
{
    /// <summary>Create, read, update and delete form definitions.</summary>
    public const string ManageForms = "manage_forms";

    /// <summary>List, read and export what visitors submitted.</summary>
    /// <remarks>
    /// Split from <see cref="ManageForms"/> because the two jobs differ. Designing a form is
    /// modelling work. Reading submissions is reading personal data that a visitor typed in, and
    /// the person who answers the contact mailbox is not usually the person who designs the form.
    /// </remarks>
    public const string ViewFormSubmissions = "view_form_submissions";

    /// <summary>
    /// The roles the module's endpoints would have listed under a <c>Roles(...)</c> gate. There was
    /// no earlier gate, so this preserves nothing; it names who the admin surface is for.
    /// </summary>
    /// <remarks>
    /// SuperAdmin holds <c>*</c>, which satisfies a capability from a module core has never heard
    /// of, so it is listed as a legacy fallback and deliberately not granted anything at seed.
    /// </remarks>
    public static readonly string[] LegacyRoles = ["Admin", "SuperAdmin"];

    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [ManageForms, ViewFormSubmissions];
}
