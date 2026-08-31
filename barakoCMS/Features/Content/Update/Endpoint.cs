using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Models;
using System.Security.Claims;

namespace barakoCMS.Features.Content.Update;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Infrastructure.Services.IContentValidatorService _validator;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, barakoCMS.Infrastructure.Services.IContentValidatorService validator, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
        _validator = validator;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirst("UserId")!.Value);
        var user = await _session.LoadAsync<User>(userId, ct);

        var existingContent = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (existingContent == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, existingContent.ContentType, "update", existingContent, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // WRITE-PATH SENSITIVITY: a caller who may not see a field may not change it. Revert any
        // such fields to their stored values before applying the update.
        await Resolve<barakoCMS.Core.Interfaces.ISensitivityService>()
            .ApplyWriteAsync(existingContent.ContentType, req.Data, existingContent.Data, HttpContext, ct);

        // DYNAMIC VALIDATION - Validate data against ContentType schema
        var validationResult = await _validator.ValidateAsync(existingContent.ContentType, req.Data);
        if (!validationResult.IsValid)
        {
            foreach (var error in validationResult.Errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }

        // DOMAIN RULES — must run on update too, or an invariant enforced at create (a balanced
        // journal entry) could simply be edited into an illegal state afterwards.
        var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(existingContent.ContentType, req.Data, existingContent.Data, userId, ct);
        if (hookErrors.Count > 0)
        {
            foreach (var error in hookErrors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
        }
        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == existingContent.ContentType, ct);

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

        var events = new List<object>();

        // 1. Data Update Event
        var updateEvent = new barakoCMS.Events.ContentUpdated(req.Id, req.Data, userId, searchText);
        events.Add(updateEvent);

        // An omitted Status means "leave it alone". Comparing against a defaulted enum instead made
        // a data-only edit look like a move to Draft and un-published the item.
        bool statusChanged = req.Status.HasValue && existingContent.Status != req.Status.Value;

        // 2. Status Change Event (if changed)
        if (statusChanged)
        {
            var statusEvent = new barakoCMS.Events.ContentStatusChanged(req.Id, req.Status!.Value, userId);
            events.Add(statusEvent);
        }

        // Best-effort early staleness check for a friendly message when the client echoes a Version.
        var state = await _session.Events.FetchStreamStateAsync(req.Id, ct);
        if (state != null && req.Version != 0 && state.Version != req.Version) // req.Version 0 means bypass check
        {
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }

        long newVersion = 0;
        try
        {
            // Atomically append with an optimistic-concurrency guard: Marten records the current
            // stream version now and rejects the commit if another writer advanced the stream first.
            await _contentWriter.AppendOptimisticAsync(existingContent, events, ct);
            await _session.SaveChangesAsync(ct);

            // Read the version back rather than deriving it from the state fetched above. That state
            // was read before the append, and when req.Version is 0 the staleness check above is
            // bypassed, so another writer could have advanced the stream in between. Deriving from
            // the stale read then under-reported the version, and the client echoing it back got a
            // spurious 412 on its next update.
            var committed = await _session.Events.FetchStreamStateAsync(req.Id, ct);
            newVersion = committed?.Version ?? (state?.Version ?? 0) + events.Count;
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }

        // Workflows are triggered out-of-band by the async WorkflowProjection reacting to the
        // committed ContentUpdated/ContentStatusChanged events — deliberately NOT awaited here.

        await Send.ResponseAsync(new Response
        {
            Id = req.Id,
            Version = newVersion,
        });
    }
}
