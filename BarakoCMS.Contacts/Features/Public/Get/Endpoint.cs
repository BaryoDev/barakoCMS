using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Contacts.Features.Public.Get;

/// <summary>GET /api/public/contacts/{id}. One published contact, Public fields only.</summary>
/// <remarks>
/// Answers 404 for everything it will not serve, so the status says nothing about which contacts
/// exist: unknown, merged, not published, or a tenant whose contact-record type is not deliverable.
/// Email, phone and address are never handed to the projector, whatever the type says.
/// </remarks>
internal sealed class PublicGetContactEndpoint(IQuerySession session, IPublicContentProjector projector)
    : EndpointWithoutRequest<PublicContentProjection>
{
    public override void Configure()
    {
        Get("/api/public/contacts/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var schema = await ContactRecord.SchemaAsync(session, ct);
        var contact = projector.IsDeliverable(schema) ? await session.LoadAsync<Contact>(Route<Guid>("id"), ct) : null;
        if (contact is not { IsPublished: true, MergedInto: null })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var served = await projector.ProjectAsync(ContactRecord.ForDelivery(contact), schema, ct);
        if (served is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(served, ct);
    }
}
