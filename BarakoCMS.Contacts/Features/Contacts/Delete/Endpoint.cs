using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Delete;

/// <summary>DELETE /api/contacts/{id}. Removes a contact and every link it is part of.</summary>
internal sealed class DeleteContactEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/contacts/{id}");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        if (await session.LoadAsync<Contact>(id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.DeleteWhere<ContactLink>(l => l.PersonId == id || l.OrganizationId == id);
        session.Delete<Contact>(id);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
