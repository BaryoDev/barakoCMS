using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Links.List;

/// <summary>GET /api/contacts/{id}/links. A person's organizations, or an organization's people, paged.</summary>
internal sealed class ListLinksEndpoint(IQuerySession session)
    : Endpoint<ListLinksRequest, PaginatedResponse<ContactLinkResponse>>
{
    public override void Configure()
    {
        Get("/api/contacts/{id}/links");
        Definition.RequireCapability(ContactsCapabilities.ReadContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(ListLinksRequest req, CancellationToken ct)
    {
        var contact = await session.LoadAsync<Contact>(req.Id, ct);
        if (contact is null || contact.MergedInto is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var id = contact.Id;
        var page = await session.Query<ContactLink>()
            .Where(l => l.PersonId == id || l.OrganizationId == id)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ContactLinkResponse>
        {
            Items = page.Items.Select(ContactLinkResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
