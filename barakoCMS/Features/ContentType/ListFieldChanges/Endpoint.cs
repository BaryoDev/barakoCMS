using barakoCMS.Features.ContentType.ChangeField;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.ListFieldChanges;

internal sealed class Request : PaginatedRequest
{
}

/// <summary>GET /api/content-types/{name}/field-changes: the type's field changes, newest first.</summary>
/// <remarks>
/// How an operator finds a change that completed with skips, or one that is stuck, without its id.
/// Ordered by when the change started, whatever sort order the request names. Paged like the
/// uniqueness duplicates route. A type is the tenant's own or one its account owns, since an account
/// type is recomputed in each tenant too. An unknown type, or another tenant's or account's, is a
/// 404; a type that has had no change is an empty page.
/// </remarks>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<FieldChangeResponse>>
{
    public override void Configure()
    {
        Get("/api/content-types/{name}/field-changes");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        if (await AccountContentTypes.ResolveAsync(session, name, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var page = await session.Query<FieldChange>()
            .Where(c => c.ContentType.ToLower() == name)
            .OrderByDescending(c => c.StartedAt)
            .ThenBy(c => c.Id)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<FieldChangeResponse>
        {
            Items = page.Items.Select(FieldChangeResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
