using BarakoCMS.Accounting.Domain;
using FastEndpoints;
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
        Roles("Accountant", "Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
        {
            AddError("Code and Name are required.");
            await SendErrorsAsync(400, ct);
            return;
        }

        var existing = await _session.Query<Account>().FirstOrDefaultAsync(a => a.Code == req.Code, ct);
        if (existing is not null)
        {
            await SendAsync(new Result { Code = req.Code, Created = false }, cancellation: ct);
            return;
        }

        _session.Store(new Account
        {
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            Type = req.Type,
            ParentCode = req.ParentCode,
            MemberId = req.MemberId,
            PayeeName = req.PayeeName
        });
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Result { Code = req.Code, Created = true }, 201, ct);
    }
}

/// <summary>GET /api/accounting/accounts — list the chart of accounts.</summary>
public class ListAccountsEndpoint : EndpointWithoutRequest<IReadOnlyList<Account>>
{
    private readonly IQuerySession _session;
    public ListAccountsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/accounting/accounts");
        Roles("Accountant", "Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var accounts = await _session.Query<Account>().OrderBy(a => a.Code).ToListAsync(ct);
        await SendAsync(accounts.ToList(), cancellation: ct);
    }
}
