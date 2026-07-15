using FastEndpoints;

namespace BarakoCMS.Accounting.Features.Reports;

/// <summary>GET /api/accounting/balances?asOf=yyyy-MM-dd — trial-balance-style listing.</summary>
public class BalancesEndpoint : Endpoint<BalancesEndpoint.Request, IReadOnlyList<AccountBalance>>
{
    private readonly ReportingService _reports;
    public BalancesEndpoint(ReportingService reports) => _reports = reports;

    public class Request { public DateOnly? AsOf { get; set; } }

    public override void Configure()
    {
        Get("/api/accounting/balances");
        Roles("Accountant", "Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
        => await SendAsync(await _reports.BalancesAsync(req.AsOf, ct), cancellation: ct);
}

/// <summary>GET /api/accounting/accounts/{code}/ledger — a single account's ledger with running balance.</summary>
public class AccountLedgerEndpoint : Endpoint<AccountLedgerEndpoint.Request, AccountLedger>
{
    private readonly ReportingService _reports;
    public AccountLedgerEndpoint(ReportingService reports) => _reports = reports;

    public class Request { public string Code { get; set; } = string.Empty; }

    public override void Configure()
    {
        Get("/api/accounting/accounts/{code}/ledger");
        Roles("Accountant", "Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var ledger = await _reports.AccountLedgerAsync(req.Code, ct);
        if (ledger is null) { await SendNotFoundAsync(ct); return; }
        await SendAsync(ledger, cancellation: ct);
    }
}
