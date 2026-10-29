using barakoCMS.Infrastructure.Auth;
using FastEndpoints;

namespace BarakoCMS.Accounting.Features.Reports;

/// <summary>GET /api/accounting/balances?asOf=yyyy-MM-dd — trial-balance-style listing.</summary>
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public class BalancesEndpoint(
    ReportingService reports) : Endpoint<BalancesEndpoint.Request, IReadOnlyList<AccountBalance>>
{
    public class Request { public DateOnly? AsOf { get; set; } }

    public override void Configure()
    {
        Get("/api/accounting/balances");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
        => await Send.ResponseAsync(await reports.BalancesAsync(req.AsOf, ct), cancellation: ct);
}

/// <summary>GET /api/accounting/accounts/{code}/ledger — a single account's ledger with running balance.</summary>
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public class AccountLedgerEndpoint(ReportingService reports) : Endpoint<AccountLedgerEndpoint.Request, AccountLedger>
{
    public class Request { public string Code { get; set; } = string.Empty; }

    public override void Configure()
    {
        Get("/api/accounting/accounts/{code}/ledger");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var ledger = await reports.AccountLedgerAsync(req.Code, ct);
        if (ledger is null) { await Send.NotFoundAsync(ct); return; }
        await Send.ResponseAsync(ledger, cancellation: ct);
    }
}

/// <summary>
/// GET /api/accounting/cash-flow?year=2026&amp;month=10&amp;cash=1010,1020 returns receipts and disbursements
/// of one month over the listed cash accounts, with the opening and closing balance.
/// </summary>
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public class CashFlowEndpoint(
    ReportingService reports, AccountService accounts) : Endpoint<CashFlowEndpoint.Request, CashFlow>
{
    private const int MaxCodes = 20;

    public class Request
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string Cash { get; set; } = string.Empty;
    }

    public override void Configure()
    {
        Get("/api/accounting/cash-flow");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var codes = req.Cash.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        if (req.Year is < 1 or > 9999)
            AddError("year must be between 1 and 9999.");
        if (req.Month is < 1 or > 12)
            AddError("month must be between 1 and 12.");
        if (codes.Count == 0)
            AddError("cash needs at least one account code.");
        else if (codes.Count > MaxCodes)
            AddError($"cash takes at most {MaxCodes} account codes.");
        else
        {
            foreach (var code in codes)
            {
                if (await accounts.GetByCodeAsync(code, ct) is null)
                    AddError($"Account '{code}' is not in the chart.");
            }
        }

        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        await Send.ResponseAsync(await reports.CashFlowAsync(codes, req.Year, req.Month, ct), cancellation: ct);
    }
}
