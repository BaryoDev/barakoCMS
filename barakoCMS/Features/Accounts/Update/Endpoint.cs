using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.Update;

/// <summary>PUT /api/accounts/{slug}: change an account's name and console branding (platform manager only).</summary>
/// <remarks>
/// The implicit default account is stored the first time it is updated. Colours left out of the
/// request stay as they are, and an empty logoUrl clears the logo.
/// </remarks>
internal class Endpoint(IDocumentSession session) : Endpoint<AccountWriteRequest, AccountResponse>
{
    public override void Configure()
    {
        Put("/api/accounts/{slug}");
        Definition.RequireCapability(SystemCapabilities.ManageTenants, "SuperAdmin");
    }

    public override async Task HandleAsync(AccountWriteRequest req, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is not { IsGlobal: true }) { await Send.ForbiddenAsync(ct); return; }

        var account = await DefaultAccount.FindAsync(session, Route<string>("slug") ?? string.Empty, ct);
        if (account is null) { await Send.NotFoundAsync(ct); return; }

        account.Name = req.Name.Trim();
        account.LogoUrl = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim();
        if (req.Colours is not null)
            account.Colours = req.Colours;

        session.Store(account);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(AccountResponse.From(account), ct);
    }
}
