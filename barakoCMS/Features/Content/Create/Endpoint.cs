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

        // WRITE-PATH SENSITIVITY: drop any sensitive fields this caller may not see, so they cannot
        // inject values into fields that would be masked from them on read.
        Resolve<barakoCMS.Core.Interfaces.ISensitivityService>()
            .ApplyWrite(req.ContentType, req.Data, existing: null, HttpContext);

        // DYNAMIC VALIDATION
        var validationResult = await _validator.ValidateAsync(req.ContentType, req.Data);
        if (!validationResult.IsValid)
        {
            await SendAsync(new Response { Message = "Validation Failed: " + string.Join(", ", validationResult.Errors) }, 400, ct);
            return;
        }

        // DOMAIN RULES. Schema validation answers "is this the right shape"; a module's lifecycle hook
        // answers "is this legal" (e.g. a journal entry's debits must equal its credits) and may
        // enrich the entry (e.g. stamp the next sequence number). Runs after validation so a hook can
        // trust the field types it reads.
        var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(req.ContentType, req.Data, existing: null, userId, ct);
        if (hookErrors.Count > 0)
        {
            await SendAsync(new Response { Message = "Validation Failed: " + string.Join(", ", hookErrors) }, 400, ct);
            return;
        }

        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == req.ContentType, ct);

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchText = string.Join(
            ' ',
            req.Data
                .Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));

        var contentId = Guid.NewGuid();
        var @event = new barakoCMS.Events.ContentCreated(contentId, req.ContentType, req.Data, req.Status, userId, searchText);

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
