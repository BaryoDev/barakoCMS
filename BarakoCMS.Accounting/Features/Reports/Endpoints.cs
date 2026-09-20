using barakoCMS.Infrastructure.Auth;
using FastEndpoints;

namespace BarakoCMS.Accounting.Features.Reports;

/// <summary>GET /api/accounting/balances?asOf=yyyy-MM-dd — trial-balance-style listing.</summary>
public class BalancesEndpoint(
    ReportingService reports) : Endpoint<BalancesEndpoint.Request, IReadOnlyList<AccountBalance>>
{
    public class Request { public DateOnly? AsOf { get; set; } }

    public override void Configure()
    {
        Get("/api/accounting/balances");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
        => await Send.ResponseAsync(await reports.BalancesAsync(req.AsOf, ct), cancellation: ct);
}

/// <summary>GET /api/accounting/accounts/{code}/ledger — a single account's ledger with running balance.</summary>
public class AccountLedgerEndpoint(ReportingService reports) : Endpoint<AccountLedgerEndpoint.Request, AccountLedger>
{
    public class Request { public string Code { get; set; } = string.Empty; }

    public override void Configure()
    {
        Get("/api/accounting/accounts/{code}/ledger");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var ledger = await reports.AccountLedgerAsync(req.Code, ct);
        if (ledger is null) { await Send.NotFoundAsync(ct); return; }
        await Send.ResponseAsync(ledger, cancellation: ct);
    }
}
