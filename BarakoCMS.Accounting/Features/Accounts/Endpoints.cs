using barakoCMS.Infrastructure.Auth;
using BarakoCMS.Accounting.Domain;
using FastEndpoints;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Accounting.Features.Accounts;

/// <summary>POST /api/accounting/accounts — create a chart-of-accounts entry.</summary>
public class CreateAccountEndpoint : Endpoint<CreateAccountEndpoint.Request, CreateAccountEndpoint.Result>
{
    private readonly IDocumentSession _session;
    public CreateAccountEndpoint(IDocumentSession session) => _session = session;

    public class Request
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public AccountType Type { get; set; }
        public string? ParentCode { get; set; }
        public Guid? MemberId { get; set; }
        public string? PayeeName { get; set; }
    }

    public class Result
    {
        public string Code { get; set; } = string.Empty;
        public bool Created { get; set; }
    }

    public override void Configure()
    {
        Post("/api/accounting/accounts");
        Definition.RequireCapability(
            AccountingCapabilities.PostEntries, AccountingCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
        {
            AddError("Code and Name are required.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Accounts are a content type now. Written here as content so this endpoint and the generic
        // /api/contents endpoint share one store — otherwise an account created through this route
        // would be invisible to reporting, which reads content.
        var chart = await AccountingContentReader.AccountsAsync(_session, ct);
        if (chart.Any(a => string.Equals(a.Code, req.Code, StringComparison.OrdinalIgnoreCase)))
        {
            await Send.ResponseAsync(new Result { Code = req.Code, Created = false }, cancellation: ct);
            return;
        }

        _session.Store(new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = AccountingContentTypes.Account,
            Status = barakoCMS.Models.ContentStatus.Published,
            Sensitivity = barakoCMS.Models.SensitivityLevel.Public,
            Data = new Dictionary<string, object>
            {
                ["Code"] = req.Code.Trim(),
                ["Name"] = req.Name.Trim(),
                ["Type"] = req.Type.ToString(),
                ["ParentCode"] = req.ParentCode ?? string.Empty,
                ["MemberId"] = req.MemberId?.ToString() ?? string.Empty,
                ["PayeeName"] = req.PayeeName ?? string.Empty,
                ["IsActive"] = true,
            },
        });
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Result { Code = req.Code, Created = true }, 201, ct);
    }
}

/// <summary>GET /api/accounting/accounts — list the chart of accounts.</summary>
public class ListAccountsEndpoint : Endpoint<barakoCMS.Models.ListRequest, barakoCMS.Models.PaginatedResponse<Account>>
{
    private readonly IQuerySession _session;
    public ListAccountsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/accounting/accounts");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(barakoCMS.Models.ListRequest req, CancellationToken ct)
    {
        // Paged in memory: accounts are read back out of content documents rather than queried as
        // their own table, so there is no IQueryable to page against.
        var accounts = await AccountingContentReader.AccountsAsync(_session, ct);
        await Send.ResponseAsync(
            accounts.OrderBy(a => a.Code).ToList().ToPagedResponse(req), cancellation: ct);
    }
}
