using System.Globalization;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for updating fields on content items.
/// Supports updating the triggering content or other content by ID.
/// </summary>
[WorkflowActionMetadata(
    Description = "Update fields on content items (status, data fields, etc.)",
    RequiredParameters = new[] { "Field", "Value" },
    ExampleJson = @"{""Type"":""UpdateField"",""Parameters"":{""Field"":""data.Status"",""Value"":""Approved""}}"
)]
internal class UpdateFieldAction : IWorkflowAction
{
    /// <summary>
    /// How many stale <see cref="barakoCMS.Models.WorkflowFieldApplyMarker"/> rows one call removes.
    /// </summary>
    /// <remarks>
    /// Bounded so a single UpdateField invocation, which every other action here treats as fast and
    /// synchronous, cannot turn into a sweep over an unbounded backlog. A deployment that somehow
    /// accumulates more stale rows than this drains it over several calls rather than one, which is
    /// the same trade-off <c>WorkflowRunRetentionService</c> makes with its own batch size, just
    /// smaller: this runs inline with every call rather than once an hour.
    /// </remarks>
    internal const int PruneBatchSize = 50;

    /// <summary>How long a marker is worth keeping.</summary>
    /// <remarks>
    /// A marker is only ever consulted while its own run can still be reclaimed and rerun, and that
    /// ends the moment the run reaches a terminal status: <c>WorkflowRunner.RunOnceAsync</c> only
    /// ever selects a Pending or Running run, so a terminal run's attempts are never revisited. The
    /// worst case for how long that can take is <c>WorkflowRetryPolicy.MaxAttempts</c> (5) attempts,
    /// each gated by up to <c>LeaseDuration</c> (5 minutes) plus the backoff between them (up to 10
    /// minutes, with jitter): comfortably under two hours.
    ///
    /// Do not widen this to match <c>WorkflowRunRetentionService</c>'s multi-day windows (7 days
    /// succeeded, 90 failed, both configurable) by pattern-matching the two together: that policy
    /// answers a different question, how long a finished run is worth an operator's while to look
    /// at, not how long an attempt can still be reclaimed. A run row surviving past this window does
    /// not extend it either, since a terminal run is never picked up again regardless of whether its
    /// row still exists. A marker outliving its own run by weeks is dead weight with nothing left to
    /// guard, so this stays measured in hours.
    /// </remarks>
    internal static readonly TimeSpan MarkerRetention = TimeSpan.FromHours(24);

    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly ILogger<UpdateFieldAction> _logger;

    /// <summary>
    /// Creates a new UpdateFieldAction.
    /// </summary>
    public UpdateFieldAction(IDocumentSession session, IContentWriter contentWriter, ILogger<UpdateFieldAction> logger)
    {
        _session = session;
        _contentWriter = contentWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "UpdateField";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var targetIdStr = parameters.GetValueOrDefault("TargetId");
        var field = parameters.GetValueOrDefault("Field");
        var value = parameters.GetValueOrDefault("Value");

        if (string.IsNullOrEmpty(field))
        {
            _logger.LogWarning("UpdateField action missing required 'Field' parameter");
            return;
        }

        try
        {
            var targetId = !string.IsNullOrEmpty(targetIdStr) && Guid.TryParse(targetIdStr, out var parsedTargetId)
                ? parsedTargetId
                : content.Id;

            // Reloaded rather than trusting the caller's copy, even when the target is the
            // triggering content the runner already loaded. The runner loads it once, right after
            // claiming the lease; if this node then runs past that lease, another node can claim,
            // run and commit before this call gets far enough to check anything, and only a load
            // taken now can see that commit.
            var targetContent = await _session.LoadAsync<barakoCMS.Models.Content>(targetId, ct);
            if (targetContent == null)
            {
                _logger.LogWarning("Target content {TargetId} not found", targetId);
                return;
            }

            // The runner injects these onto every parameter set (see WorkflowRunner.ExecuteAsync);
            // an action invoked some other way, a legacy engine or a test, leaves them out, and the
            // guard below is simply skipped for it, exactly as it always ran before this existed.
            string? idempotencyKey = null;
            var attemptNumber = 0;
            barakoCMS.Models.WorkflowFieldApplyMarker? marker = null;

            if (parameters.TryGetValue("IdempotencyKey", out var rawKey)
                && !string.IsNullOrWhiteSpace(rawKey)
                && parameters.TryGetValue("Attempt", out var rawAttempt)
                && int.TryParse(rawAttempt, NumberStyles.Integer, CultureInfo.InvariantCulture, out attemptNumber))
            {
                idempotencyKey = rawKey;

                // IdempotencyKey is stable across every rerun of this one action (it is derived
                // from the run id and the ordinal), so this key alone cannot tell a reclaimed rerun
                // of attempt 2 from the genuine attempt 3 that follows a real failure. Attempt can:
                // it only advances once WorkflowRunner records a terminal outcome, which is exactly
                // the write a reclaimed attempt never gets to make. Two executions of the same
                // attempt carry the same Attempt value; a retry after a real failure carries the
                // next one.
                marker = await _session.LoadAsync<barakoCMS.Models.WorkflowFieldApplyMarker>(idempotencyKey, ct);

                if (marker is not null && marker.Attempt == attemptNumber)
                {
                    _logger.LogInformation(
                        "UpdateField attempt {Attempt} of {IdempotencyKey} was already applied to {TargetId}; skipping.",
                        attemptNumber, idempotencyKey, targetId);
                    return;
                }
            }

            var events = new List<object>();
            var dataChanged = false;

            // Handle nested field paths (e.g., "data.AssignedTo")
            if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            {
                var dataKey = field.Substring(5);
                targetContent.Data[dataKey] = value;
                dataChanged = true;
            }
            else if (field.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<barakoCMS.Models.ContentStatus>(value, true, out var newStatus))
                {
                    events.Add(new barakoCMS.Events.ContentStatusChanged(targetContent.Id, newStatus, content.LastModifiedBy, DateTime.UtcNow));
                }
            }
            else
            {
                // Default to data field
                targetContent.Data[field] = value;
                dataChanged = true;
            }

            if (dataChanged)
            {
                // Data is replaced wholesale by Content.Apply(ContentUpdated, ...), so this carries
                // the field just set alongside everything already on the document.
                events.Insert(0, new barakoCMS.Events.ContentUpdated(
                    targetContent.Id, targetContent.Data, content.LastModifiedBy, targetContent.SearchText, DateTime.UtcNow));
            }

            if (events.Count == 0)
            {
                // Nothing to apply: an unrecognised Status value with no marker to record either.
                return;
            }

            if (idempotencyKey is not null)
            {
                // Staged into the same session as the content write below, so one SaveChangesAsync
                // commits the field change, this marker, and the stale ones swept up here together,
                // or none of them: Marten commits a session as one transaction across document
                // types, which is what makes the marker safe to keep in a document of its own
                // rather than needing it folded into the content write to stay atomic with it.
                await PruneStaleMarkersAsync(ct);

                marker ??= new barakoCMS.Models.WorkflowFieldApplyMarker { Key = idempotencyKey };
                marker.Attempt = attemptNumber;
                marker.AppliedAt = DateTimeOffset.UtcNow;
                _session.Store(marker);
            }

            // Optimistic rather than a plain Store: a document type keeps last-write-wins today,
            // but this stops being true the moment a type turns on optimistic concurrency, and
            // nothing here can assume it never will.
            await _contentWriter.AppendOptimisticAsync(targetContent, events, ct);
            await _session.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Updated field {Field} on content {ContentId} to value {Value}",
                field, targetContent.Id, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update field {Field}", field);
        }
    }

    /// <summary>
    /// Stages the removal of up to <see cref="PruneBatchSize"/> markers past
    /// <see cref="MarkerRetention"/>, in whatever tenant partition this action's own session is
    /// scoped to.
    /// </summary>
    /// <remarks>
    /// Queued onto the caller's session rather than committed here: this only runs where the caller
    /// is about to call <c>SaveChangesAsync</c> itself, right after staging its own marker and
    /// content change, so the removals land in that same commit rather than one of their own.
    /// </remarks>
    private async Task PruneStaleMarkersAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - MarkerRetention;

        var stale = await _session.Query<barakoCMS.Models.WorkflowFieldApplyMarker>()
            .Where(m => m.AppliedAt < cutoff)
            .Take(PruneBatchSize)
            .ToListAsync(ct);

        foreach (var m in stale)
        {
            _session.Delete(m);
        }
    }
}
