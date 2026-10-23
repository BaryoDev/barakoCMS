using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Contacts.Merge;

/// <summary>POST /api/contacts/{id}/merge. Folds a duplicate into the contact that stays.</summary>
/// <remarks>
/// This is the documented way out of the 409 a duplicate email answers. The survivor keeps every
/// value it has and takes the duplicate's where it has none, including custom fields. The
/// duplicate's links move to the survivor, and a link the survivor already has is not doubled. The
/// duplicate stays as a tombstone naming the survivor, with its personal fields cleared, so a
/// reference to its id can still be followed.
///
/// One save, so a failure leaves both contacts as they were and the merge can be retried. Marten
/// applies the writes in the order they are queued, so the duplicate gives up its email key before
/// the survivor takes the address. Both contacts must be the same kind. Contacts are versioned: a
/// contact changed by someone else since it was read fails the save and answers 409, which is also
/// what stops A merged into B and B merged into A from both succeeding.
/// </remarks>
internal sealed class MergeContactEndpoint(IDocumentSession session) : Endpoint<MergeContactRequest, ContactResponse>
{
    public override void Configure()
    {
        Post("/api/contacts/{id}/merge");
        Definition.RequireCapability(ContactsCapabilities.ManageContacts, ContactsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(MergeContactRequest req, CancellationToken ct)
    {
        var source = await session.LoadAsync<Contact>(req.Id, ct);
        var target = await session.LoadAsync<Contact>(req.IntoId, ct);
        if (source is null || target is null || source.MergedInto is not null || target.MergedInto is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (source.Kind != target.Kind)
        {
            AddError(r => r.IntoId, "A person and an organization cannot be merged.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var emailKey = source.EmailKey;
        var email = source.Email;
        source.EmailKey = null;
        source.MergedInto = target.Id;

        Fill(source, target, email, emailKey);
        await MoveLinksAsync(source.Id, target.Id, ct);
        ClearPersonal(source);
        session.Update(source);
        session.Update(target);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ContactSupport.IsUniqueViolation(ex) || ContactSupport.IsConcurrency(ex))
        {
            ThrowError("A contact changed during the merge, and nothing was merged. Read both contacts and merge again.", 409);
        }

        await Send.OkAsync(ContactSupport.ToResponse(target), ct);
    }

    private static void Fill(Contact source, Contact target, string? email, string? emailKey)
    {
        target.GivenName ??= source.GivenName;
        target.FamilyName ??= source.FamilyName;
        target.Website ??= source.Website;
        target.Phone ??= source.Phone;
        target.Address ??= source.Address;
        if (target.EmailKey is null)
        {
            target.Email = email;
            target.EmailKey = emailKey;
        }

        foreach (var (name, value) in source.Fields)
        {
            target.Fields.TryAdd(name, value);
        }

        target.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearPersonal(Contact source)
    {
        source.Email = null;
        source.Phone = null;
        source.Address = null;
        source.GivenName = null;
        source.FamilyName = null;
        source.Website = null;
        source.Fields = new();
        source.IsPublished = false;
        source.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task MoveLinksAsync(Guid fromId, Guid toId, CancellationToken ct)
    {
        var links = await session.Query<ContactLink>()
            .Where(l => l.PersonId == fromId || l.OrganizationId == fromId)
            .ToListAsync(ct);

        foreach (var link in links)
        {
            var person = link.PersonId == fromId ? toId : link.PersonId;
            var organization = link.OrganizationId == fromId ? toId : link.OrganizationId;
            session.Delete(link);

            var moved = ContactLink.IdFor(person, organization);
            if (await session.LoadAsync<ContactLink>(moved, ct) is null)
            {
                session.Store(new ContactLink
                {
                    Id = moved,
                    PersonId = person,
                    OrganizationId = organization,
                    Role = link.Role,
                    CreatedAt = link.CreatedAt,
                });
            }
        }
    }
}
