namespace BarakoCMS.Accounting;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module and a third-party module is not in this repository at all. Nothing validates a
/// capability name on the way into a role, so a name a module declares is grantable the day the
/// module ships. See issue #443.
/// </remarks>
public static class AccountingCapabilities
{
    /// <summary>Read the chart of accounts, the balances and a single account's ledger.</summary>
    public const string ViewLedger = "view_ledger";

    /// <summary>Create an account and post a journal entry.</summary>
    /// <remarks>
    /// Split from <see cref="ViewLedger"/> even though the old gate was one role list for all five
    /// routes. Reading the books and writing to them are the two halves every accounting system
    /// separates, and an auditor who may read the ledger without being able to post to it is the
    /// ordinary case rather than an exotic one. One name makes it unexpressible.
    /// </remarks>
    public const string PostEntries = "post_journal_entries";

    /// <summary>
    /// The roles that reached these endpoints before the migration, which is exactly what the old
    /// <c>Roles("Accountant", "Admin", "SuperAdmin")</c> gate listed.
    /// </summary>
    /// <remarks>
    /// SuperAdmin is not here: it holds <c>*</c>, which satisfies a capability from a module core has
    /// never heard of, so granting it explicitly would be bookkeeping with nothing behind it.
    /// </remarks>
    public static readonly string[] LegacyRoles = ["Accountant", "Admin", "SuperAdmin"];

    internal static readonly string[] SeededRoles = ["Accountant", "Admin"];

    internal static readonly string[] All = [ViewLedger, PostEntries];
}
