using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Get;

internal class Endpoint(
    IQuerySession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    IContentSourcingPolicy sourcing) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/contents/{id}");
        // Authenticated only. Anonymous reads go through the delivery API (/api/public/{type}/{slug}),
        // which serves published entries and public fields; this is the authoring read.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // A person or a service principal. Neither claim present answers 401.
        Models.User? user = await barakoCMS.Infrastructure.Auth.ActingCaller.LoadAsync(session, User, ct);
        if (barakoCMS.Infrastructure.Auth.ActingCaller.IdOf(User) is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var content = await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        // An entry of another branch is hidden as one that does not exist.
        if (content == null || !await Resolve<barakoCMS.Infrastructure.Services.IBranchScopeGuard>().MayAccessAsync(User, content, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 2. Authorize Read
        if (user == null || !await permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            // 403 Forbidden
            await Send.ForbiddenAsync(ct);
            return;
        }

        Response = await EntryResponse.BuildAsync(
            content, session, sourcing, Resolve<ISensitivityService>(), HttpContext, ct, Resolve<IFileStore>());
    }
}
