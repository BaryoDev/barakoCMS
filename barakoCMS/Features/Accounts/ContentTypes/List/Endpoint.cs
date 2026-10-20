using barakoCMS.Features.ContentType;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.List;

/// <summary>GET /api/accounts/{slug}/content-types: the content types the account owns.</summary>
internal class Endpoint(IQuerySession session) : Endpoint<PaginatedRequest, PaginatedResponse<ContentTypeResponse>>
{
    public override void Configure()
    {
        Get("/api/accounts/{slug}/content-types");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var account = await AccountOwnedTypes.ManagedAsync(
            HttpContext, session, Route<string>("slug") ?? string.Empty,
            () => Send.ForbiddenAsync(ct), () => Send.NotFoundAsync(ct), ct);
        if (account is null) return;

        var page = await session.Query<AccountContentType>()
            .Where(t => t.AccountId == account.Id)
            .OrderBy(t => t.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ContentTypeResponse>
        {
            Items = page.Items.Select(t => ContentTypeResponse.From(t.Definition, false, account.Id)).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
