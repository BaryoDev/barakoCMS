using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Get;

/// <summary>GET /api/contacts/{id}. One contact, personal fields included. A merged contact answers 404.</summary>
internal sealed class GetContactEndpoint(IQuerySession session) : EndpointWithoutRequest<ContactResponse>
{
    public override void Configure()
    {
        Get("/api/contacts/{id}");
        Definition.RequireCapability(ContactsCapabilities.ReadContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var contact = await session.LoadAsync<Contact>(Route<Guid>("id"), ct);
        if (contact is null || contact.MergedInto is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ContactSupport.ToResponse(contact), ct);
    }
}
