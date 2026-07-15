namespace BarakoCMS.Accounting.Domain;

/// <summary>
/// One posting line of a journal entry. By convention exactly one of <see cref="Debit"/> /
/// <see cref="Credit"/> is non-zero, and both are non-negative.
/// </summary>
public class JournalLine
{
    public string AccountCode { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Memo { get; set; }
}

public enum JournalStatus
{
    Posted,
    Voided
}

/// <summary>
/// A balanced double-entry journal entry. Lines are embedded, so the whole entry is one Marten
/// document written in a single transaction — debit and credit can never commit separately.
///
/// Entries are immutable once posted: corrections are made by posting a reversing entry
/// (<see cref="VoidsEntryId"/>) rather than editing, preserving an audit trail.
/// </summary>
public class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Sequential, human-facing entry number (e.g. "JE-2026-000123").</summary>
    public string EntryNumber { get; set; } = string.Empty;

    /// <summary>Accounting date of the entry (may differ from <see cref="CreatedAt"/>).</summary>
    public DateOnly Date { get; set; }

    public string Memo { get; set; } = string.Empty;

    /// <summary>Optional grouping reference, e.g. a batch id for "District Levy 2026-07".</summary>
    public string? Reference { get; set; }

    public List<JournalLine> Lines { get; set; } = new();

    public JournalStatus Status { get; set; } = JournalStatus.Posted;

    /// <summary>When this entry reverses another, the id of the entry it voids.</summary>
    public Guid? VoidsEntryId { get; set; }

    /// <summary>Total debits (equal to total credits) — denormalised for quick display/sorting.</summary>
    public decimal Amount { get; set; }

    /// <summary>Ids of attached files (receipts, deposit slips, photos) stored via a file module.</summary>
    public List<string> Attachments { get; set; } = new();

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
