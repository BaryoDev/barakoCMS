using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Content.Types;

/// <summary>
/// GET /api/contents/types/{name}, the shape of one content type for a caller who may read it, so a
/// client can see the fields before it writes an entry.
/// </summary>
/// <remarks>
/// A type the caller may not read answers 404, the same as a name that does not exist. The check is
/// the type-level read with no entry, so a rule limited to own entries still gets the shape.
/// </remarks>
internal sealed class GetEndpoint(IQuerySession session, IPermissionResolver permissionResolver)
    : EndpointWithoutRequest<ReadableTypeResponse>
{
    public override void Configure() => Get("/api/contents/types/{name}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await barakoCMS.Infrastructure.Auth.ActingCaller.LoadAsync(session, User, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var name = Route<string>("name") ?? string.Empty;
        var definition = await AccountContentTypes.ResolveAsync(session, name, ct);
        if (definition is null || !await permissionResolver.CanPerformActionAsync(user, definition.Name, "read", null, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ReadableTypeResponse.From(definition), ct);
    }
}
