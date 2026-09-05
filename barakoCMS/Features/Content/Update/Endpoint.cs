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
    private readonly IContentSourcingPolicy _sourcing;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Infrastructure.Services.IContentValidatorService validator,
        IContentWriter contentWriter,
        IContentSourcingPolicy sourcing,
        IConfiguration configuration,
        ILogger<Endpoint> logger)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
        _validator = validator;
        _sourcing = sourcing;
        _configuration = configuration;
        _logger = logger;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Configure() calls Claims("UserId"), so a request with no claim never reaches here, and the
        // token issuer only ever writes a Guid. Parsing defensively anyway matches Create and turns
        // a token this server did not mint into a 400 rather than an unhandled FormatException.
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("User ID claim not found");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid User ID format");
        }

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
        var updateEvent = new barakoCMS.Events.ContentUpdated(req.Id, req.Data, userId, searchText, DateTime.UtcNow);
        events.Add(updateEvent);

        // An omitted Status means "leave it alone". Comparing against a defaulted enum instead made
        // a data-only edit look like a move to Draft and un-published the item.
        bool statusChanged = req.Status.HasValue && existingContent.Status != req.Status.Value;

        // 2. Status Change Event (if changed)
        if (statusChanged)
        {
            var statusEvent = new barakoCMS.Events.ContentStatusChanged(req.Id, req.Status!.Value, userId, DateTime.UtcNow);
            events.Add(statusEvent);
        }

        // Which contract this request is answered under. The persistence decision is the writer's
        // and is read there; this is the status code, and the two types genuinely answer the same
        // request differently. Decision 3 of #230: an event-sourced type is on expected-version and
        // returns 409, and every other type keeps last-write-wins, because moving a type from
        // last-write-wins to expected-version later is a break clients never handled and moving the
        // other way breaks nothing. Documented in docs/event-sourced-content-types.md rather than
        // treated as an inconsistency to tidy up later.
        var eventSourced = await _sourcing.IsEventSourcedAsync(existingContent.ContentType, ct);

        // Best-effort early staleness check for a friendly message when the client echoes a Version.
        // Document types only: on an event-sourced type the writer refuses the same request below,
        // and a 412 from here would beat it to the answer.
        var state = await _session.Events.FetchStreamStateAsync(req.Id, ct);
        if (!eventSourced && state != null && req.Version != 0 && state.Version != req.Version) // req.Version 0 means bypass check
        {
            ThrowError(e => e.Version, "The content has been modified by another user. Please refresh and try again.", 412);
        }

        // #565 / D16: the document itself (as opposed to the event stream above, which only
        // event-sourced types are on) gets Marten's own optimistic concurrency. Ships unconditionally
        // for every content type via GET's ETag / PUT's If-Match; Content:Concurrency:Require only
        // decides what happens to a caller that sends neither. Not applied to event-sourced types,
        // which already refused a stale or missing version above; checking twice would just be two
        // ways to answer the same request differently depending on which check ran first.
        Guid? expectedDocVersion = null;
        if (!eventSourced)
        {
            var ifMatchHeader = HttpContext.Request.Headers.IfMatch.ToString();
            Guid? claimedVersion = null;
            if (!string.IsNullOrEmpty(ifMatchHeader))
            {
                if (!ContentETag.TryParse(ifMatchHeader, out var parsed))
                {
                    ThrowError("If-Match is not a valid ETag.", 400);
                }

                claimedVersion = parsed;
            }

            var currentMetadata = await _session.MetadataForAsync(existingContent, ct);

            if (claimedVersion is { } claimed && currentMetadata?.CurrentVersion != claimed)
            {
                ThrowError("The content has been modified by another user. Please refresh and try again.", 412);
            }

            if (claimedVersion is null && _configuration.GetValue("Content:Concurrency:Require", false))
            {
                ThrowError("If-Match is required to update this content. GET the entry and send its ETag back as If-Match.", 428);
            }

            if (claimedVersion is null)
            {
                // Content:Concurrency:Require is off (the 4.x default), so a caller that sends
                // nothing is not refused, which is the 3.x upgrade path this flag exists to protect.
                // Logged because an operator deciding when to flip the default to true in 5.0 needs
                // to see how many writes still arrive with nothing to check.
                _logger.LogWarning(
                    "Content:Concurrency:Require is off and accepted a write to {ContentId} with no If-Match.",
                    req.Id);
            }

            // Bound to what was just read, not to claimedVersion. By this point the two are proven
            // equal whenever claimedVersion is not null (the check above already refused a mismatch),
            // so using the fresh read either way binds the write to reality rather than to the
            // client's word for it, and covers a caller that sent nothing: without this, a request
            // with no If-Match would fall back to true last-write-wins even against a writer that
            // commits later in this same request, which is exactly the race D16 says "turn on
            // optimistic concurrency" has to catch on its own, independent of what the caller sent.
            expectedDocVersion = currentMetadata?.CurrentVersion;
        }

        long newVersion = 0;
        try
        {
            // Atomically append with an optimistic-concurrency guard: Marten records the current
            // stream version now and rejects the commit if another writer advanced the stream first.
            //
            // Version 0 means the client sent none. For a document type that stays a bypass, which
            // is what every client relies on today. For an event-sourced type it is itself a refusal:
            // the stream is the record, so a write that cannot say where the stream was is not a
            // write anyone can reason about.
            await _contentWriter.AppendAsync(existingContent, events, req.Version == 0 ? null : req.Version, ct);

            // After the writer, not before. IContentWriter's document-mode path re-loads the document
            // internally to copy committed state onto the caller's instance before its own Store, and
            // that second load is what this has to win against: registering the expected version any
            // earlier gets superseded by it, and the check would silently never fire. Verified in the
            // mutation check on this PR: disabling this line leaves
            // ContentConcurrencyTests.Two_racing_updates_with_no_version_sent_one_succeeds_one_is_refused
            // red (the write silently wins instead of the second writer being refused) while the rest
            // of the class stays green, since the stale-If-Match case is refused earlier, above.
            if (expectedDocVersion is { } expected)
            {
                _session.UpdateExpectedVersion(existingContent, expected);
            }

            await _session.SaveChangesAsync(ct);

            // Read the version back rather than deriving it from the state fetched above. That state
            // was read before the append, and when req.Version is 0 the staleness check above is
            // bypassed, so another writer could have advanced the stream in between. Deriving from
            // the stale read then under-reported the version, and the client echoing it back got a
            // spurious 412 on its next update.
            var committed = await _session.Events.FetchStreamStateAsync(req.Id, ct);
            newVersion = committed?.Version ?? (state?.Version ?? 0) + events.Count;

            if (!eventSourced)
            {
                var newMetadata = await _session.MetadataForAsync(existingContent, ct);
                if (newMetadata is not null)
                {
                    HttpContext.Response.Headers.ETag = ContentETag.Format(newMetadata.CurrentVersion);
                }
            }
        }
        catch (StaleContentException ex)
        {
            ThrowError(e => e.Version, ex.Message, 409);
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
