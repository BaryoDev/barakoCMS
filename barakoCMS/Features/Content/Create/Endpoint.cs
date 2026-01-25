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
    private readonly barakoCMS.Features.Workflows.IWorkflowEngine _workflowEngine;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IContentValidatorService validator, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, barakoCMS.Features.Workflows.IWorkflowEngine workflowEngine)
    {
        _session = session;
        _validator = validator;
        _permissionResolver = permissionResolver;
        _workflowEngine = workflowEngine;
    }

    public override void Configure()
    {
        Post("/api/contents");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        try
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

            // Start event stream and store document in a single transaction
            _session.Events.StartStream<barakoCMS.Models.Content>(contentId, @event);

            var content = new barakoCMS.Models.Content();
            content.Apply(@event);
            _session.Store(content);

            // Single SaveChanges for atomicity - both event and document saved together
            await _session.SaveChangesAsync(ct);

            // WORKFLOW TRIGGER
            await _workflowEngine.ProcessEventAsync(req.ContentType, "create", content, ct);

            await SendAsync(new Response
            {
                Id = contentId,
                Message = "Content created successfully"
            });
        }
        catch (Exception ex)
        {
            // Log detailed error for debugging (server-side only)
            Console.WriteLine($"[CREATE CONTENT ERROR] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[CREATE CONTENT INNER] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Console.WriteLine($"[CREATE CONTENT STACK] {ex.StackTrace}");

            // Send generic error to client - don't expose internal details
            await SendAsync(new Response { Message = "An error occurred while creating content. Please try again." }, 500, ct);
        }
    }
}
