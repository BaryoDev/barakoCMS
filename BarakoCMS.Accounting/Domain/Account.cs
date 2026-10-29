namespace BarakoCMS.Accounting.Domain;

/// <summary>
/// The five classical account classes. Normal balance side is derived from this:
/// Asset and Expense are debit-normal; Liability, Equity and Income are credit-normal.
/// </summary>
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense
}

/// <summary>
/// A single account in the club's chart of accounts. Stored as a strongly-typed Marten document
/// (not a barakoCMS content type) so money-adjacent code works with real <see cref="decimal"/>
/// values and Marten can aggregate balances in SQL.
///
/// A member's receivable is modelled as a child account whose <see cref="ParentCode"/> points at
/// the "Accounts Receivable - Members" control account (e.g. code "1100-3322657", parent "1100"),
/// with <see cref="MemberId"/> linking back to the member content item.
/// </summary>
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-facing account code, unique across the chart (e.g. "1000", "1100-3322657").</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    /// <summary>Code of the control/parent account, or null for a top-level account.</summary>
    public string? ParentCode { get; set; }

    /// <summary>Set when this is a member's personal sub-account; links to the member content item id.</summary>
    public Guid? MemberId { get; set; }

    /// <summary>Set when this is an external payee's sub-account (RI, District, magazine, etc.).</summary>
    public string? PayeeName { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When true, no entry may take this account's balance below zero, and the same holds for every
    /// account beneath it (by <see cref="ParentCode"/>). Off by default.
    /// </summary>
    public bool NoOverdraft { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when this account increases with a debit (Asset, Expense).</summary>
    public bool IsDebitNormal => Type is AccountType.Asset or AccountType.Expense;
}
