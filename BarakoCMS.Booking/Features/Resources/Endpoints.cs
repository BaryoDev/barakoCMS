using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Booking.Features.Resources;

internal static class ResourceMapping
{
    public static void Apply(BookingResource resource, ResourceRequest req)
    {
        resource.Name = req.Name.Trim();
        resource.EntryType = string.IsNullOrWhiteSpace(req.EntryType) ? null : req.EntryType.Trim();
        resource.EntryId = req.EntryId;
        resource.TimeZone = req.TimeZone.Trim();
        resource.SlotMinutes = req.SlotMinutes;
        resource.Active = req.Active;
        resource.OpeningHours = req.OpeningHours
            .Select(h => new OpeningWindow { Day = Enum.Parse<DayOfWeek>(h.Day, true).ToString(), Start = h.Start, End = h.End })
            .ToList();
        resource.Exceptions = req.Exceptions
            .Select(e => new ResourceException
            {
                Date = e.Date,
                Closed = e.Closed,
                Hours = e.Closed ? [] : (e.Hours ?? []).Select(h => new DayWindow { Start = h.Start, End = h.End }).ToList(),
            })
            .ToList();
    }
}

/// <summary>GET /api/booking/resources: the tenant's resources, paged.</summary>
internal sealed class ListEndpoint(IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<ResourceResponse>>
{
    public override void Configure()
    {
        Get("/api/booking/resources");
        Definition.RequireCapability(BookingCapabilities.ManageResources, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await session.Query<BookingResource>()
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Id)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ResourceResponse>
        {
            Items = page.Items.Select(ResourceResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

/// <summary>GET /api/booking/resources/{id}.</summary>
internal sealed class GetEndpoint(IQuerySession session) : EndpointWithoutRequest<ResourceResponse>
{
    public override void Configure()
    {
        Get("/api/booking/resources/{id}");
        Definition.RequireCapability(BookingCapabilities.ManageResources, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resource = await session.LoadAsync<BookingResource>(Route<Guid>("id"), ct);
        if (resource is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ResourceResponse.From(resource), ct);
    }
}

/// <summary>POST /api/booking/resources: creates a resource.</summary>
internal sealed class CreateEndpoint(IDocumentSession session) : Endpoint<ResourceRequest, ResourceResponse>
{
    public override void Configure()
    {
        Post("/api/booking/resources");
        Definition.RequireCapability(BookingCapabilities.ManageResources, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ResourceRequest req, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var resource = new BookingResource { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        ResourceMapping.Apply(resource, req);

        session.Store(resource);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(ResourceResponse.From(resource), 201, ct);
    }
}

/// <summary>PUT /api/booking/resources/{id}: replaces a resource. A value left out goes back to its default.</summary>
internal sealed class UpdateEndpoint(IDocumentSession session) : Endpoint<ResourceRequest, ResourceResponse>
{
    public override void Configure()
    {
        Put("/api/booking/resources/{id}");
        Definition.RequireCapability(BookingCapabilities.ManageResources, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ResourceRequest req, CancellationToken ct)
    {
        var resource = await session.LoadAsync<BookingResource>(Route<Guid>("id"), ct);
        if (resource is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        ResourceMapping.Apply(resource, req);
        resource.UpdatedAt = DateTimeOffset.UtcNow;

        session.Store(resource);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(ResourceResponse.From(resource), ct);
    }
}

/// <summary>
/// DELETE /api/booking/resources/{id}. Refused with 409 while a booking still holds time on the
/// resource; cancel those first, or set the resource inactive instead.
/// </summary>
internal sealed class DeleteEndpoint(IDocumentSession session, IOptions<BookingOptions> options) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/booking/resources/{id}");
        Definition.RequireCapability(BookingCapabilities.ManageResources, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        if (await session.LoadAsync<BookingResource>(id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if ((await BookedStretches.HeldAsync(session, options.Value, id, null, ct)).Count > 0)
        {
            ThrowError("The resource still has bookings. Cancel them first, or set the resource inactive.", 409);
        }

        session.Delete<BookingResource>(id);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
