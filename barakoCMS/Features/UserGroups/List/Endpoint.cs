using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.List;

internal class Endpoint(
    IDocumentSession session) : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.UserGroups.UserGroupResponse>>
{
    public override void Configure()
    {
        Get("/api/user-groups");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<UserGroup>()
            .OrderBy(g => g.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<barakoCMS.Features.UserGroups.UserGroupResponse>
        {
            Items = page.Items.Select(barakoCMS.Features.UserGroups.UserGroupResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
