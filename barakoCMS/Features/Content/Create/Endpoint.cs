using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Create;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentValidatorService _validator;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IContentValidatorService validator, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _session = session;
        _validator = validator;
        _permissionResolver = permissionResolver;
    }

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
            await SendAsync(new Response { Message = "User ID claim not found" }, 400, ct);
            return;
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendAsync(new Response { Message = "Invalid User ID format" }, 400, ct);
            return;
        }

        // PERMISSION CHECK
        var user = await _session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            await SendAsync(new Response { Message = "User not found" }, 401, ct);
            return;
        }

        if (!await _permissionResolver.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        // DYNAMIC VALIDATION
        var validationResult = await _validator.ValidateAsync(req.ContentType, req.Data);
        if (!validationResult.IsValid)
        {
            await SendAsync(new Response { Message = "Validation Failed: " + string.Join(", ", validationResult.Errors) }, 400, ct);
            return;
        }

        var contentId = Guid.NewGuid();
        var @event = new barakoCMS.Events.ContentCreated(contentId, req.ContentType, req.Data, req.Status, userId);

        // Start the event stream AND store the read-model document in one transaction so they
        // can't diverge on a partial failure. Unhandled errors flow to the global exception handler.
        _session.Events.StartStream<barakoCMS.Models.Content>(contentId, @event);
        var content = new barakoCMS.Models.Content();
        content.Apply(@event);
        _session.Store(content);
        await _session.SaveChangesAsync(ct);

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentCreated event — deliberately NOT awaited here, so a slow or failing
        // workflow action can never block or fail the content save.

        await SendAsync(new Response
        {
            Id = contentId,
            Version = 1,
            Message = "Content created successfully"
        });
    }
}
