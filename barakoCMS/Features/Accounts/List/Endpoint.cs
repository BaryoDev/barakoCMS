using barakoCMS.Models;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.List;

internal class Request : PaginatedRequest
{
}

/// <summary>GET /api/accounts: every account for a platform manager, or the accounts a scoped manager has been made manager of.</summary>
/// <remarks>
/// The implicit default account is listed first, so a deployment with no accounts still sees the
/// one that holds its tenants. Then the stored accounts by name.
/// </remarks>
internal class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<AccountResponse>>
{
    public override void Configure()
    {
        Get("/api/accounts");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is null) { await Send.ForbiddenAsync(ct); return; }

        var stored = reach.IsGlobal
            ? session.Query<TenantAccount>()
            : session.Query<TenantAccount>().Where(a => reach.AccountIds.Contains(a.Id));
        var storedCount = await stored.CountAsync(ct);
        var defaultStored = await stored.AnyAsync(a => a.Id == TenantAccount.DefaultId, ct);
        var showsImplicit = reach.Covers(TenantAccount.DefaultId) && !defaultStored;

        // The default account leads, stored or not, so paging stays stable when it gets a document.
        var ordered = stored.Where(a => a.Id != TenantAccount.DefaultId).OrderBy(a => a.Name);
        var items = new List<TenantAccount>();
        var skip = req.Skip;
        var take = req.Take;
        var leadsWithDefault = reach.Covers(TenantAccount.DefaultId);

        if (leadsWithDefault)
        {
            if (skip == 0)
            {
                items.Add(defaultStored
                    ? await stored.FirstAsync(a => a.Id == TenantAccount.DefaultId, ct)
                    : DefaultAccount.Implicit());
                take--;
            }
            else
            {
                skip--;
            }
        }

        if (take > 0)
            items.AddRange(await ordered.Skip(skip).Take(take).ToListAsync(ct));

        await Send.OkAsync(new PaginatedResponse<AccountResponse>
        {
            Items = items.Select(AccountResponse.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = storedCount + (showsImplicit ? 1 : 0),
        }, ct);
    }
}
