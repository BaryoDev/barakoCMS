using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Create;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Services.IContentCreator creator) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/contents");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

        var user = await session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            ThrowError("User not found", 401);
        }

        if (!await permissionResolver.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Sensitivity, validation and lifecycle hooks, then the write. The same path an import runs,
        // so what one route refuses the other refuses too.
        var request = new barakoCMS.Infrastructure.Services.ContentCreateRequest
        {
            ContentType = req.ContentType,
            Data = req.Data,
            Status = req.Status,
            Sensitivity = req.Sensitivity,
        };

        var errors = await creator.CheckAsync(request, userId, HttpContext, batch: null, ct);
        if (errors.Count > 0)
        {
            // One entry per failure rather than one flattened string, so a client can show the
            // failures against the fields they belong to.
            foreach (var error in errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }

        var created = await creator.StageAsync(request, userId, batch: null, ct);

        await session.SaveChangesAsync(ct);

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentCreated event — deliberately NOT awaited here, so a slow or failing
        // workflow action can never block or fail the content save.

        await Send.ResponseAsync(new Response
        {
            Id = created.Id,
            Version = 1,
        });
    }
}
