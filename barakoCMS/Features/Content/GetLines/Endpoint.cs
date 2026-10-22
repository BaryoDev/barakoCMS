using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.GetLines;

/// <summary>
/// GET /api/contents/{id}/lines, which reads a header with all of its lines. The lines are one
/// query on the session, which filters by tenant, so a header of another tenant is a 404 and its
/// lines are never reached.
/// </summary>
internal class Endpoint(
    IQuerySession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    IContentSourcingPolicy sourcing) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/contents/{id}/lines");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await session.LoadAsync<Models.User>(userId, ct);
        var header = await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (header is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user is null || !await permissionResolver.CanPerformActionAsync(user, header.ContentType, "read", header, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var typeName = barakoCMS.Core.ContentTypeName.Normalize(header.ContentType);
        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == typeName, ct);
        if (definition?.Lines is not { } lines)
        {
            AddError("This content type declares no lines");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var children = await LineTotals.ChildrenAsync(session, lines, header.Id, LineTotals.MaxLines + 1, ct);

        var sensitivity = Resolve<ISensitivityService>();
        var files = Resolve<IFileStore>();
        var response = new Response
        {
            Header = await barakoCMS.Features.Content.Get.EntryResponse.BuildAsync(header, session, sourcing, sensitivity, HttpContext, ct, files),
        };
        foreach (var child in children)
        {

            if (!await permissionResolver.CanPerformActionAsync(user, child.ContentType, "read", child, ct))
                continue;
            if (response.Lines.Count == LineTotals.MaxLines)
            {
                response.Truncated = true;
                break;
            }

            response.Lines.Add(await barakoCMS.Features.Content.Get.EntryResponse.BuildAsync(child, session, sourcing, sensitivity, HttpContext, ct, files));
        }

        Response = response;
    }
}
