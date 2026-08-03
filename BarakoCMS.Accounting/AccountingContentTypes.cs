using barakoCMS.Models;

namespace BarakoCMS.Accounting;

/// <summary>
/// The accounting domain modelled as ordinary barakoCMS content types, per the project's
/// content-type-first rule: if something can be a content type it should be, so it stays pluggable,
/// queryable and deliverable through the generic endpoints instead of a bespoke API surface.
///
/// The one thing a schema cannot express — "total debits must equal total credits" — lives in
/// <see cref="JournalEntryHook"/>, a content lifecycle hook that runs inside the generic write
/// pipeline. So the entries are ordinary content AND the ledger still cannot be put into an
/// unbalanced state.
/// </summary>
public static class AccountingContentTypes
{
    public const string Account = "account";
    public const string JournalEntry = "journalEntry";

    /// <summary>Valid <c>Type</c> values on an account — the five classical account classes.</summary>
    public static readonly IReadOnlyList<string> AccountTypes =
        new[] { "Asset", "Liability", "Equity", "Income", "Expense" };

    /// <summary>Accounts that increase with a debit. The rest are credit-normal.</summary>
    public static bool IsDebitNormal(string? accountType) =>
        string.Equals(accountType, "Asset", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(accountType, "Expense", StringComparison.OrdinalIgnoreCase);

    public static ContentTypeDefinition AccountDefinition() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000ACC00010"),
        Name = Account,
        DisplayName = "Account",
        Description = "One account in the chart of accounts.",
        Fields = new List<FieldDefinition>
        {
            new() { Name = "Code", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Name", Type = "string", Sensitivity = SensitivityLevel.Public },
            // Stored as a string rather than an enum type: the field registry has no enum type yet
            // (roadmap F.3), and the hook rejects anything outside AccountTypes.
            new() { Name = "Type", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "ParentCode", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "MemberId", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "PayeeName", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "IsActive", Type = "bool", Sensitivity = SensitivityLevel.Public },
        },
    };

    public static ContentTypeDefinition JournalEntryDefinition() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000ACC00011"),
        Name = JournalEntry,
        DisplayName = "Journal Entry",
        Description = "A balanced double-entry posting. Immutable once posted; correct by reversing.",
        Fields = new List<FieldDefinition>
        {
            // Stamped by JournalEntryHook on create, not supplied by the caller.
            new() { Name = "EntryNumber", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Date", Type = "date", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Memo", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Reference", Type = "string", Sensitivity = SensitivityLevel.Public },
            // Each line: { AccountCode, Debit, Credit, Memo }. Debit/Credit are decimals and stay
            // exact through the JSON round trip — see ObjectJsonConverter.
            new() { Name = "Lines", Type = "array", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Status", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "VoidsEntryId", Type = "string", Sensitivity = SensitivityLevel.Public },
            // Denormalised total (equal to total debits), stamped by the hook for display/sorting.
            new() { Name = "Amount", Type = "decimal", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Attachments", Type = "array", Sensitivity = SensitivityLevel.Public },
        },
    };
}
