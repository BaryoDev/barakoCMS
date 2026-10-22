using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Update;

/// <summary>PUT /api/contacts/{id}. Replaces a contact. A merged contact is gone and answers 404. A contact with links keeps its kind.</summary>
internal sealed class UpdateContactEndpoint(IDocumentSession session, IContentValidatorService validator)
    : Endpoint<UpdateContactRequest, ContactResponse>
{
    public override void Configure()
    {
        Put("/api/contacts/{id}");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(UpdateContactRequest req, CancellationToken ct)
    {
        var contact = await session.LoadAsync<Contact>(req.Id, ct);
        if (contact is null || contact.MergedInto is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var kind = contact.Kind;
        ContactSupport.Apply(contact, req);
        if (contact.Kind != kind
            && await session.Query<ContactLink>().AnyAsync(l => l.PersonId == contact.Id || l.OrganizationId == contact.Id, ct))
        {
            ThrowError("A contact with links cannot change kind. Remove its links first.", 409);
        }

        contact.IsPublished = req.IsPublished;

        var errors = await ContactSupport.SchemaErrorsAsync(session, validator, contact, User, ct);
        if (errors.Count > 0)
        {
            errors.ForEach(e => ValidationFailures.Add(new ValidationFailure("fields", e)));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (contact.EmailKey is { } key
            && await session.Query<Contact>().FirstOrDefaultAsync(c => c.EmailKey == key && c.Id != contact.Id, ct) is { } holder)
        {
            ThrowError($"Contact {holder.Id} already has this email. Merge the two instead.", 409);
        }

        session.Update(contact);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ContactSupport.IsUniqueViolation(ex))
        {
            ThrowError("Another contact has just taken this email. Merge the two instead.", 409);
        }
        catch (Exception ex) when (ContactSupport.IsConcurrency(ex))
        {
            ThrowError("The contact changed while you were saving it. Read it again and retry.", 409);
        }

        await Send.OkAsync(ContactSupport.ToResponse(contact), ct);
    }
}
