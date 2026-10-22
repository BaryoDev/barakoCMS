using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Create;

/// <summary>POST /api/contacts. Creates a person or an organization.</summary>
/// <remarks>
/// An address already held by another contact in the tenant answers 409 and names that contact, so
/// the caller can update it or merge into it. The read is a courtesy. What makes the rule hold is
/// the unique index on the address key: two creates of one address that race both pass the read, and
/// the second save loses on the index and is answered 409 too, never 500.
/// </remarks>
internal sealed class CreateContactEndpoint(IDocumentSession session, IContentValidatorService validator)
    : Endpoint<CreateContactRequest, ContactResponse>
{
    public override void Configure()
    {
        Post("/api/contacts");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(CreateContactRequest req, CancellationToken ct)
    {
        var contact = new Contact { Id = Guid.NewGuid(), CreatedBy = ContactSupport.CallerId(User) };
        ContactSupport.Apply(contact, req);

        var errors = await ContactSupport.SchemaErrorsAsync(session, validator, contact, User, ct);
        if (errors.Count > 0)
        {
            errors.ForEach(e => ValidationFailures.Add(new ValidationFailure("fields", e)));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (contact.EmailKey is { } key
            && await session.Query<Contact>().FirstOrDefaultAsync(c => c.EmailKey == key, ct) is { } holder)
        {
            ThrowError($"Contact {holder.Id} already has this email. Update it, or merge into it.", 409);
        }

        session.Insert(contact);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ContactSupport.IsUniqueViolation(ex))
        {
            ThrowError("Another contact has just taken this email. Update that one, or merge into it.", 409);
        }

        await Send.ResponseAsync(ContactSupport.ToResponse(contact), 201, ct);
    }
}
