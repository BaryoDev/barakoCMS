using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Get;

internal class Endpoint(
    IQuerySession session) : Endpoint<ListRequest, PaginatedResponse<barakoCMS.Features.ContentType.ContentTypeResponse>>
{
    public override void Configure()
    {
        // The content-type resource lived at two route names: read at /api/schemas, create at
        // POST /api/content-types, and the delivery toggle at /api/content-types/{name}. It is
        // consolidated on /api/content-types. /api/schemas stays as a deprecated alias so an
        // existing client keeps working; it goes in 6.0.
        Get("/api/content-types", "/api/schemas");
        // NOTE: AllowAnonymous() must NOT be combined with Roles() — in ASP.NET Core
        // AllowAnonymous short-circuits authorization and silently disables the role check.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var (accountId, shared) = await barakoCMS.Infrastructure.Services.AccountContentTypes.ForTenantAsync(session, ct);
        var ownedByAccount = new HashSet<Guid>(shared.Select(t => t.Definition.Id));

        PaginatedResponse<ContentTypeDefinition> page;
        if (shared.Count == 0)
        {
            page = await session.Query<ContentTypeDefinition>()
                .OrderBy(x => x.Name)
                .ToPagedResponseAsync(req, ct);
        }
        else
        {
            // The tenant's own types and the account's, in one list ordered by name. A tenant's own
            // type of a name the account owns is shadowed by it only on create; one that already
            // exists is listed beside it.
            var own = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
            var all = own.Concat(shared.Select(t => t.Definition))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
            page = all.ToPagedResponse(req);
        }

        // One load for the page rather than one per type. The policies are keyed by the same name
        // the definitions carry, and a name with no policy is not event sourced.
        var policies = page.Items.Count == 0
            ? new List<ContentTypeSourcingPolicy>()
            : (await session.LoadManyAsync<ContentTypeSourcingPolicy>(
                page.Items.Select(x => x.Name).ToArray())).ToList();

        var eventSourced = policies
            .Where(p => p.EventSourced)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A field's role list is stored as role ids and answered as role names, which is what a
        // client shows and sends back. The definitions come from a query session, so rewriting
        // them here stores nothing.
        await barakoCMS.Core.RoleReferences.ToNamesAsync(session, page.Items.SelectMany(d => d.Fields ?? []), ct);

        await Send.OkAsync(new PaginatedResponse<barakoCMS.Features.ContentType.ContentTypeResponse>
        {
            Items = page.Items
                .Select(d => barakoCMS.Features.ContentType.ContentTypeResponse.From(
                    d, eventSourced.Contains(d.Name), ownedByAccount.Contains(d.Id) ? accountId : null))
                .ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
