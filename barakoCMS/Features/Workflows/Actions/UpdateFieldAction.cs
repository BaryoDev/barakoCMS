using System.Globalization;
using System.Text.Json;
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
    /// The reserved <c>Data</c> key an applied attempt marks itself with, so a reclaimed attempt
    /// that reruns after its outcome was discarded (see the comment above the lease check in
    /// <c>WorkflowRunner.TryRunAsync</c>) finds its own mark already there and writes nothing a
    /// second time.
    /// </summary>
    /// <remarks>
    /// Kept on the content itself rather than in a new document, because the marker has to commit
    /// in the same write as the field it guards: if either could land without the other, the node
    /// that runs past its lease could still apply twice with no mark, or mark without applying.
    /// It never reaches public delivery, because <c>PublicDelivery.PublicData</c> only forwards
    /// fields a content type's schema declares Public, and no schema declares this one.
    ///
    /// Internal rather than private so a test can build the exact key and value this action reads,
    /// the same reason <c>WorkflowRetryPolicy</c>'s constants are internal: reached through
    /// <c>InternalsVisibleTo</c> rather than by duplicating the literal format in a test and hoping
    /// it never drifts.
    /// </remarks>
    internal const string AppliedMarkerPrefix = "__workflow.updateField.applied:";

    /// <summary>How long a marker is worth keeping.</summary>
    /// <remarks>
    /// A marker is only ever consulted while its own run can still be reclaimed and rerun, and that
    /// ends the moment the run reaches a terminal status: <c>WorkflowRunner.RunOnceAsync</c> only
    /// ever selects a Pending or Running run, so a terminal run's attempts are never revisited. The
    /// worst case for how long that can take is <c>WorkflowRetryPolicy.MaxAttempts</c> (5) attempts,
    /// each gated by up to <c>LeaseDuration</c> (5 minutes) plus the backoff between them (up to 10
    /// minutes, with jitter): comfortably under two hours. This is generously past that, not the
    /// multi-day window <c>WorkflowRunRetentionService</c> uses to keep a run record readable, since
    /// that answers a different question (how long a finished run is worth an operator's while to
    /// look at), and a marker outliving its own run by weeks is dead weight with nothing left to
    /// guard.
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
            string? markerKey = null;
            string? attempt = null;
            if (parameters.TryGetValue("IdempotencyKey", out var idempotencyKey)
                && !string.IsNullOrWhiteSpace(idempotencyKey)
                && parameters.TryGetValue("Attempt", out attempt)
                && !string.IsNullOrWhiteSpace(attempt))
            {
                markerKey = AppliedMarkerPrefix + idempotencyKey;

                // IdempotencyKey is stable across every rerun of this one action (it is derived
                // from the run id and the ordinal), so this key alone cannot tell a reclaimed rerun
                // of attempt 2 from the genuine attempt 3 that follows a real failure. Attempt can:
                // it only advances once WorkflowRunner records a terminal outcome, which is exactly
                // the write a reclaimed attempt never gets to make. Two executions of the same
                // attempt carry the same Attempt value; a retry after a real failure carries the
                // next one.
                if (targetContent.Data.TryGetValue(markerKey, out var recorded)
                    && TryParseMarker(AsMarkerString(recorded), out var recordedAttempt, out _)
                    && string.Equals(recordedAttempt, attempt, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "UpdateField attempt {Attempt} of {IdempotencyKey} was already applied to {TargetId}; skipping.",
                        attempt, idempotencyKey, targetId);
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

            if (markerKey is not null)
            {
                // Pruned here, in the same event as the write it guards, rather than by a separate
                // sweep: a marker's Data key never touches anything else this system reads, so
                // nothing but another call to this action would ever remove it, and "decrement
                // stock on every order" calls this action once per order, forever.
                PruneStaleMarkers(targetContent.Data, DateTimeOffset.UtcNow);
                targetContent.Data[markerKey] = FormatMarker(attempt!, DateTimeOffset.UtcNow);
                dataChanged = true;
            }

            if (dataChanged)
            {
                // Data is replaced wholesale by Content.Apply(ContentUpdated, ...), so this carries
                // the field just set alongside everything already on the document, marker included.
                events.Insert(0, new barakoCMS.Events.ContentUpdated(
                    targetContent.Id, targetContent.Data, content.LastModifiedBy, targetContent.SearchText, DateTime.UtcNow));
            }

            if (events.Count == 0)
            {
                // Nothing to apply: an unrecognised Status value with no marker to record either.
                return;
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

    /// <summary>The marker as a plain string, whether it just round-tripped through Marten's JSON or not.</summary>
    private static string? AsMarkerString(object? stored) => stored switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => stored.ToString(),
    };

    /// <summary>The attempt number and when it was recorded, packed so both survive one Data value.</summary>
    internal static string FormatMarker(string attempt, DateTimeOffset at) =>
        $"{attempt}|{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Unpacks a marker written by <see cref="FormatMarker"/>. False for anything else, garbled or
    /// not: <see cref="PruneStaleMarkers"/> then removes it rather than keeping a value nothing can
    /// read the age of.
    /// </summary>
    private static bool TryParseMarker(string? raw, out string attempt, out DateTimeOffset at)
    {
        attempt = string.Empty;
        at = DateTimeOffset.MinValue;

        if (string.IsNullOrEmpty(raw)) return false;

        var parts = raw.Split('|', 2);
        if (parts.Length != 2) return false;

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            return false;
        }

        attempt = parts[0];
        at = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        return true;
    }

    /// <summary>Removes every marker on <paramref name="data"/> older than <see cref="MarkerRetention"/>.</summary>
    /// <remarks>
    /// Keys are collected before any are removed, since removing from a dictionary while enumerating
    /// its own <c>Keys</c> throws. A marker in the old, bare-attempt-number shape (nothing this
    /// deployment ever wrote once this ran) fails to parse and is treated the same as one that is
    /// simply too old, rather than kept forever because its age cannot be read.
    /// </remarks>
    private static void PruneStaleMarkers(Dictionary<string, object> data, DateTimeOffset now)
    {
        List<string>? stale = null;

        foreach (var key in data.Keys)
        {
            if (!key.StartsWith(AppliedMarkerPrefix, StringComparison.Ordinal)) continue;

            if (TryParseMarker(AsMarkerString(data[key]), out _, out var at) && now - at <= MarkerRetention)
            {
                continue;
            }

            (stale ??= new List<string>()).Add(key);
        }

        if (stale is null) return;

        foreach (var key in stale)
        {
            data.Remove(key);
        }
    }
}
