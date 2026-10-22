using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Links.Add;

/// <summary>POST /api/contacts/{id}/links. Links a person to an organization.</summary>
/// <remarks>
/// Linking the same pair again changes the role and answers 200, so a retry is safe. A link joins a
/// person to an organization and nothing else, and both must be contacts of the caller's tenant: an
/// id from another tenant is simply not found.
/// </remarks>
internal sealed class AddLinkEndpoint(IDocumentSession session) : Endpoint<AddLinkRequest, ContactLinkResponse>
{
    public override void Configure()
    {
        Post("/api/contacts/{id}/links");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(AddLinkRequest req, CancellationToken ct)
    {
        var person = await session.LoadAsync<Contact>(req.Id, ct);
        var organization = await session.LoadAsync<Contact>(req.OrganizationId, ct);
        if (person is null || organization is null || person.MergedInto is not null || organization.MergedInto is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (person.Kind != ContactKind.Person || organization.Kind != ContactKind.Organization)
        {
            AddError(r => r.OrganizationId, "A link joins a Person to an Organization.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var id = ContactLink.IdFor(person.Id, organization.Id);
        var existing = await session.LoadAsync<ContactLink>(id, ct);
        var link = existing ?? new ContactLink { Id = id, PersonId = person.Id, OrganizationId = organization.Id };
        link.Role = ContactSupport.Clean(req.Role);

        session.Store(link);
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(ContactLinkResponse.From(link), existing is null ? 201 : 200, ct);
    }
}
