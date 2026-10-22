using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Update;

/// <summary>PUT /api/contacts/{id}. Replaces a contact. A merged contact is gone and answers 404.</summary>
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

        ContactSupport.Apply(contact, req);
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

        await Send.OkAsync(ContactSupport.ToResponse(contact), ct);
    }
}
