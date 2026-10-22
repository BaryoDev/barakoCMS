using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Public.List;

/// <summary>GET /api/public/contacts. Published contacts, Public fields only, paged.</summary>
/// <remarks>Answers an empty page, not an error, when the type is not deliverable.</remarks>
internal sealed class PublicListContactsEndpoint(IQuerySession session, IPublicContentProjector projector)
    : Endpoint<PaginatedRequest, PaginatedResponse<PublicContentProjection>>
{
    public override void Configure()
    {
        Get("/api/public/contacts");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PaginatedRequest req, CancellationToken ct)
    {
        var schema = await ContactRecord.SchemaAsync(session, ct);
        if (!projector.IsDeliverable(schema))
        {
            await Send.OkAsync(new PaginatedResponse<PublicContentProjection> { Page = req.Page, PageSize = req.PageSize }, ct);
            return;
        }

        var page = await session.Query<Contact>()
            .Where(c => c.IsPublished && c.MergedInto == null)
            .OrderBy(c => c.DisplayName).ThenBy(c => c.Id)
            .ToPagedResponseAsync(req, ct);

        var items = new List<PublicContentProjection>();
        foreach (var contact in page.Items)
        {
            if (await projector.ProjectAsync(ContactRecord.ForDelivery(contact), schema, ct) is { } served)
            {
                items.Add(served);
            }
        }

        await Send.OkAsync(new PaginatedResponse<PublicContentProjection>
        {
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
