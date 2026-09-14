using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Manage;

/// <summary>GET /api/forms. The content types that accept public submissions, paged.</summary>
internal sealed class ListEndpoint(IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<FormResponse>>
{
    public override void Configure()
    {
        Get("/api/forms");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<PublicForm>()
            .OrderBy(f => f.ContentType)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<FormResponse>
        {
            Items = page.Items.Select(f => new FormResponse
            {
                ContentType = f.ContentType,
                Enabled = true,
                EnabledAt = f.EnabledAt,
            }).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
