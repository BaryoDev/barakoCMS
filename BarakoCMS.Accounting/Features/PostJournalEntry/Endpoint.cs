using FastEndpoints;

namespace BarakoCMS.Accounting.Features.PostJournalEntry;

public class Request
{
    public DateOnly Date { get; set; }
    public string Memo { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public List<LineDto> Lines { get; set; } = new();
    public List<string>? Attachments { get; set; }

    public class LineDto
    {
        public string AccountCode { get; set; } = string.Empty;
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string? Memo { get; set; }
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string EntryNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>POST /api/accounting/journal-entries — post one balanced journal entry.</summary>
public class Endpoint : Endpoint<Request, Response>
{
    private readonly LedgerService _ledger;
    public Endpoint(LedgerService ledger) => _ledger = ledger;

    public override void Configure()
    {
        Post("/api/accounting/journal-entries");
        Roles("Accountant", "Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var cmd = new PostEntryCommand(
            Date: req.Date == default ? DateOnly.FromDateTime(DateTime.UtcNow) : req.Date,
            Memo: req.Memo,
            Reference: req.Reference,
            Lines: req.Lines.Select(l => new PostLine(l.AccountCode, l.Debit, l.Credit, l.Memo)).ToList(),
            Attachments: req.Attachments);

        var result = await _ledger.PostAsync(cmd, userId, ct);
        if (!result.Ok)
        {
            foreach (var err in result.Errors) AddError(err);
            await SendErrorsAsync(400, ct);
            return;
        }

        await SendAsync(new Response
        {
            Id = result.Entry!.Id,
            EntryNumber = result.Entry.EntryNumber,
            Amount = result.Entry.Amount,
            Message = "Journal entry posted."
        }, 201, ct);
    }
}
