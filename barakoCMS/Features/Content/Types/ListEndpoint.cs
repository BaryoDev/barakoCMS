using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Content.Types;

/// <summary>
/// GET /api/contents/types, the names of the content types the caller may read, so a client that
/// holds only a content key can find what to ask about.
/// </summary>
/// <remarks>
/// Any signed-in caller, no capability: the answer is already limited to types the caller's role
/// may read. A literal segment, so it wins over <c>/api/contents/{id}</c>.
/// </remarks>
internal sealed class ListEndpoint(IQuerySession session, IPermissionResolver permissionResolver)
    : EndpointWithoutRequest<ReadableTypeList>
{
    public override void Configure() => Get("/api/contents/types");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await barakoCMS.Infrastructure.Auth.ActingCaller.LoadAsync(session, User, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var (_, shared) = await AccountContentTypes.ForTenantAsync(session, ct);
        var own = await session.Query<barakoCMS.Models.ContentTypeDefinition>().ToListAsync(ct);

        // The account's definition wins over a tenant's own of the same name.
        var byName = new Dictionary<string, barakoCMS.Models.ContentTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in own) byName.TryAdd(d.Name, d);
        foreach (var t in shared) byName[t.Definition.Name] = t.Definition;

        var items = new List<ReadableTypeSummary>();
        foreach (var d in byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (await permissionResolver.CanPerformActionAsync(user, d.Name, "read", null, ct))
                items.Add(new ReadableTypeSummary(d.Name, d.DisplayName));
        }

        await Send.OkAsync(new ReadableTypeList(items), ct);
    }
}
