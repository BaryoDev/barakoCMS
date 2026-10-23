using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.List;

/// <summary>GET /api/contacts. The tenant's contacts, paged, merged ones left out.</summary>
internal sealed class ListContactsEndpoint(IQuerySession session)
    : Endpoint<ListContactsRequest, PaginatedResponse<ContactResponse>>
{
    public override void Configure()
    {
        Get("/api/contacts");
        Definition.RequireCapability(ContactsCapabilities.ReadContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(ListContactsRequest req, CancellationToken ct)
    {
        var query = session.Query<Contact>().Where(c => c.MergedInto == null);

        if (!string.IsNullOrWhiteSpace(req.Kind))
        {
            if (!ContactSupport.TryKind(req.Kind, out var kind))
            {
                AddError(r => r.Kind!, "kind must be Person or Organization.");
                await Send.ErrorsAsync(400, ct);
                return;
            }

            query = query.Where(c => c.Kind == kind);
        }

        if (ContactSupport.EmailKey(req.Email) is { } key)
        {
            query = query.Where(c => c.EmailKey == key);
        }

        if (ContactSupport.Clean(req.Name) is { } name)
        {
            query = query.Where(c => c.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        var page = await query.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ContactResponse>
        {
            Items = page.Items.Select(ContactSupport.ToResponse).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
