using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Get;

internal class Endpoint : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.ContentType.ContentTypeResponse>>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        // The content-type resource lived at two route names: read at /api/schemas, create at
        // POST /api/content-types, and the delivery toggle at /api/content-types/{name}. It is
        // consolidated on /api/content-types. /api/schemas stays as a deprecated alias so an
        // existing client keeps working; it goes in 5.0.
        Get("/api/content-types", "/api/schemas");
        // NOTE: AllowAnonymous() must NOT be combined with Roles() — in ASP.NET Core
        // AllowAnonymous short-circuits authorization and silently disables the role check.
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<ContentTypeDefinition>()
            .OrderBy(x => x.Name)
            .ToPagedResponseAsync(req, ct);

        // One load for the page rather than one per type. The policies are keyed by the same name
        // the definitions carry, and a name with no policy is not event sourced.
        var policies = page.Items.Count == 0
            ? new List<ContentTypeSourcingPolicy>()
            : (await _session.LoadManyAsync<ContentTypeSourcingPolicy>(
                page.Items.Select(x => x.Name).ToArray())).ToList();

        var eventSourced = policies
            .Where(p => p.EventSourced)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Send.OkAsync(new PaginatedResponse<barakoCMS.Features.ContentType.ContentTypeResponse>
        {
            Items = page.Items
                .Select(d => barakoCMS.Features.ContentType.ContentTypeResponse.From(d, eventSourced.Contains(d.Name)))
                .ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
