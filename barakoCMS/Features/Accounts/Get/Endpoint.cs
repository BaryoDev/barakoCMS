using barakoCMS.Models;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.Get;

/// <summary>GET /api/accounts/{slug}: one account. An account the caller does not manage answers 404.</summary>
internal class Endpoint(IQuerySession session) : EndpointWithoutRequest<AccountResponse>
{
    public override void Configure()
    {
        Get("/api/accounts/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
        Definition.Metadata(new AccountScopedManagement());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is null) { await Send.ForbiddenAsync(ct); return; }

        var account = await DefaultAccount.FindAsync(session, Route<string>("slug") ?? string.Empty, ct);
        if (account is null || !reach.Covers(account.Id)) { await Send.NotFoundAsync(ct); return; }

        await Send.OkAsync(AccountResponse.From(account), ct);
    }
}
