using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Create;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Services.IContentValidatorService _validator;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IContentValidatorService validator, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
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
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

        // PERMISSION CHECK
        var user = await _session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            ThrowError("User not found", 401);
        }

        if (!await _permissionResolver.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // WRITE-PATH SENSITIVITY: drop any sensitive fields this caller may not see, so they cannot
        // inject values into fields that would be masked from them on read.
        await Resolve<barakoCMS.Core.Interfaces.ISensitivityService>()
            .ApplyWriteAsync(req.ContentType, req.Data, existing: null, HttpContext, ct);

        // DYNAMIC VALIDATION
        var validationResult = await _validator.ValidateAsync(req.ContentType, req.Data);
        if (!validationResult.IsValid)
        {
            // One entry per failure rather than one flattened string, so a client can show the
            // failures against the fields they belong to.
            foreach (var error in validationResult.Errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }

        // DOMAIN RULES. Schema validation answers "is this the right shape"; a module's lifecycle hook
        // answers "is this legal" (e.g. a journal entry's debits must equal its credits) and may
        // enrich the entry (e.g. stamp the next sequence number). Runs after validation so a hook can
        // trust the field types it reads.
        var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(req.ContentType, req.Data, existing: null, userId, ct);
        if (hookErrors.Count > 0)
        {
            foreach (var error in hookErrors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
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
        var @event = new barakoCMS.Events.ContentCreated(contentId, req.ContentType, req.Data, req.Status, userId, searchText, req.Sensitivity);

        var created = _contentWriter.Create(@event);

        // A type with its own lifecycle starts its entries at the state it declared. Set on the
        // document rather than carried in ContentCreated, because the event is public API under
        // section 6 and this can be derived from the type definition at any time, including on a
        // replay. Null stays null for every type that declares no lifecycle, which is all of them
        // today, and that is what keeps their behaviour unchanged.
        if (definition?.Lifecycle is { } lifecycle)
        {
            created.LifecycleState = lifecycle.InitialState;
            _session.Store(created);
        }

        await _session.SaveChangesAsync(ct);

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentCreated event — deliberately NOT awaited here, so a slow or failing
        // workflow action can never block or fail the content save.

        await Send.ResponseAsync(new Response
        {
            Id = contentId,
            Version = 1,
        });
    }
}
