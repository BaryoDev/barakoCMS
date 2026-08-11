namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Builds the payload for a journal entry, the shape posted through the generic content endpoint.
///
/// Accounting is the part of this codebase where a wrong number is worse than a crash, because a
/// crash gets noticed. The builder is written so the arithmetic is visible in the test: a balanced
/// entry says <c>.Debit(cash, 100).Credit(income, 100)</c>, and an unbalanced one — the case the
/// posting rules must reject — is obviously unbalanced on the page rather than hidden in a literal
/// array of anonymous objects.
///
/// Amounts are <see cref="decimal"/> throughout, never double. That is the rule the ledger lives by.
/// </summary>
public sealed class JournalEntryBuilder : BuilderBase<Dictionary<string, object>>
{
    private readonly List<object> _lines = new();
    private string? _entryNumber;
    private DateTime? _date;
    private string? _memo;
    private string? _reference;
    private string? _status;
    private string? _voidsEntryId;

    public JournalEntryBuilder Debit(string accountCode, decimal amount)
    {
        _lines.Add(new { AccountCode = accountCode, Debit = amount, Credit = 0m });
        return this;
    }

    public JournalEntryBuilder Credit(string accountCode, decimal amount)
    {
        _lines.Add(new { AccountCode = accountCode, Debit = 0m, Credit = amount });
        return this;
    }

    /// <summary>A line carrying both sides at once — nonsense in double-entry, so worth testing.</summary>
    public JournalEntryBuilder Line(string accountCode, decimal debit, decimal credit)
    {
        _lines.Add(new { AccountCode = accountCode, Debit = debit, Credit = credit });
        return this;
    }

    public JournalEntryBuilder Numbered(string entryNumber)
    {
        _entryNumber = entryNumber;
        return this;
    }

    public JournalEntryBuilder On(DateTime date)
    {
        _date = date;
        return this;
    }

    public JournalEntryBuilder WithMemo(string memo)
    {
        _memo = memo;
        return this;
    }

    public JournalEntryBuilder WithReference(string reference)
    {
        _reference = reference;
        return this;
    }

    public JournalEntryBuilder WithStatus(string status)
    {
        _status = status;
        return this;
    }

    /// <summary>Marks this entry as the reversal of another, which balances must then exclude.</summary>
    public JournalEntryBuilder Voiding(Guid entryId)
    {
        _voidsEntryId = entryId.ToString();
        return this;
    }

    /// <summary>Total debits minus total credits — zero for a valid entry. Lets a test state its intent.</summary>
    public decimal Imbalance()
    {
        decimal debits = 0, credits = 0;
        foreach (var l in _lines)
        {
            var t = l.GetType();
            debits += (decimal)(t.GetProperty("Debit")!.GetValue(l) ?? 0m);
            credits += (decimal)(t.GetProperty("Credit")!.GetValue(l) ?? 0m);
        }
        return debits - credits;
    }

    public override Dictionary<string, object> Build()
    {
        var data = new Dictionary<string, object>
        {
            ["EntryNumber"] = _entryNumber ?? Unique("JE"),
            ["Date"] = (_date ?? DateTime.UtcNow.Date).ToString("yyyy-MM-dd"),
            ["Lines"] = _lines.ToArray(),
        };
        if (_memo is not null) data["Memo"] = _memo;
        if (_reference is not null) data["Reference"] = _reference;
        if (_status is not null) data["Status"] = _status;
        if (_voidsEntryId is not null) data["VoidsEntryId"] = _voidsEntryId;
        return data;
    }
}
