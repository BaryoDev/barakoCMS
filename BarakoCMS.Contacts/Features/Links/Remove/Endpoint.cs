using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Links.Remove;

/// <summary>DELETE /api/contacts/{id}/links/{organizationId}. Unlinks a person from an organization.</summary>
internal sealed class RemoveLinkEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/contacts/{id}/links/{organizationId}");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = ContactLink.IdFor(Route<Guid>("id"), Route<Guid>("organizationId"));
        if (await session.LoadAsync<ContactLink>(id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete<ContactLink>(id);
        await session.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
