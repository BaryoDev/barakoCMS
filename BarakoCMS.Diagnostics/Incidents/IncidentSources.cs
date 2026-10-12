using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Diagnostics.Incidents;

/// <summary>The fingerprint the core-record sources give a fault: a short hash of what makes two failures the same.</summary>
internal static class IncidentFingerprint
{
    public static string Of(params string?[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>The exception type a stored error starts with, so the same failure with a different id still groups.</summary>
    public static string Kind(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unknown";
        var colon = error.IndexOf(':');
        var head = colon > 0 ? error[..colon] : error;
        return head.Length <= 120 ? head.Trim() : head[..120];
    }
}

/// <summary>Browser errors reported to <c>POST /api/client-errors</c>, already one row per fingerprint and tenant.</summary>
internal sealed class ClientErrorIncidentSource(IQuerySession session) : IIncidentSource
{
    public string Name => "client-errors";

    // The view's own gate is this capability, so a caller who reached the view can read these.
    public string? RequiredCapability => null;

    public async Task<IReadOnlyList<IncidentOccurrence>> ReadAsync(IncidentQuery query, CancellationToken ct)
    {
        var since = query.Since.UtcDateTime;
        var rows = session.Query<ClientError>().Where(e => e.LastSeenAt >= since);
        if (!query.AllTenants)
        {
            var slug = query.Tenant;
            rows = rows.Where(e => e.Tenant == slug);
        }

        var errors = await rows.OrderByDescending(e => e.LastSeenAt).Take(query.Limit).ToListAsync(ct);
        return errors.Select(e => new IncidentOccurrence
        {
            Fingerprint = e.Fingerprint,
            Title = e.Message,
            Detail = e.Source,
            Tenant = e.Tenant,
            Release = e.AppVersion,
            Count = Math.Max(1, e.Count),
            FirstSeenAt = new DateTimeOffset(DateTime.SpecifyKind(e.FirstSeenAt, DateTimeKind.Utc)),
            LastSeenAt = new DateTimeOffset(DateTime.SpecifyKind(e.LastSeenAt, DateTimeKind.Utc)),
            Resolved = e.Resolved,
            ResolvedAt = e.ResolvedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
            ResolvedBy = e.ResolvedBy,
            ResolutionReference = e.ResolutionReference,
            ResolutionNote = e.ResolutionNote,
        }).ToList();
    }
}

/// <summary>Background jobs that used every attempt and were dead-lettered, in the current tenant.</summary>
internal sealed class DeadLetteredJobIncidentSource(IQuerySession session) : IIncidentSource
{
    public string Name => "dead-letters";

    public string? RequiredCapability => SystemCapabilities.ViewJobs;

    public async Task<IReadOnlyList<IncidentOccurrence>> ReadAsync(IncidentQuery query, CancellationToken ct)
    {
        var since = query.Since.UtcDateTime;
        var jobs = await session.Query<JobRecord>()
            .Where(j => j.State == JobState.DeadLettered && j.CreatedAt >= since)
            .OrderByDescending(j => j.CreatedAt)
            .Take(query.Limit)
            .ToListAsync(ct);

        return jobs.Select(j =>
        {
            var command = j.CommandType.Split(',')[0];
            var kind = IncidentFingerprint.Kind(j.LastError);
            var last = new DateTimeOffset(DateTime.SpecifyKind(j.CompletedAt ?? j.CreatedAt, DateTimeKind.Utc));
            return new IncidentOccurrence
            {
                Fingerprint = IncidentFingerprint.Of("job", command, kind),
                Title = $"{command[(command.LastIndexOf('.') + 1)..]} gave up after {j.AttemptCount} attempts",
                Detail = j.LastError,
                Tenant = query.Tenant,
                FirstSeenAt = new DateTimeOffset(DateTime.SpecifyKind(j.CreatedAt, DateTimeKind.Utc)),
                LastSeenAt = last,
            };
        }).ToList();
    }
}

/// <summary>Workflow actions that failed for good: their run ended Failed or PartiallyFailed.</summary>
internal sealed class FailedWorkflowRunIncidentSource(IQuerySession session) : IIncidentSource
{
    public string Name => "failed-runs";

    public string? RequiredCapability => SystemCapabilities.ViewWorkflowRuns;

    public async Task<IReadOnlyList<IncidentOccurrence>> ReadAsync(IncidentQuery query, CancellationToken ct)
    {
        var since = query.Since;
        var runs = await session.Query<WorkflowRun>()
            .Where(r => (r.Status == RunStatus.Failed || r.Status == RunStatus.PartiallyFailed) && r.CreatedAt >= since)
            .OrderByDescending(r => r.CreatedAt)
            .Take(query.Limit)
            .ToListAsync(ct);

        return runs.SelectMany(r => r.Actions
            .Where(a => a.Status is AttemptStatus.Failed or AttemptStatus.Unknown)
            .Select(a => new IncidentOccurrence
            {
                Fingerprint = IncidentFingerprint.Of("run", r.WorkflowName, a.ActionType, IncidentFingerprint.Kind(a.Error)),
                Title = $"{r.WorkflowName}: {a.ActionType} failed",
                Detail = a.Error,
                Tenant = query.Tenant,
                FirstSeenAt = r.CreatedAt,
                LastSeenAt = a.CompletedAt ?? r.CompletedAt ?? r.CreatedAt,
            }))
            .Take(query.Limit)
            .ToList();
    }
}

/// <summary>Webhook and connector sends that got no answer, or an answer outside 2xx.</summary>
internal sealed class FailedDeliveryIncidentSource(IQuerySession session) : IIncidentSource
{
    public string Name => "failed-deliveries";

    public string? RequiredCapability => SystemCapabilities.ViewWorkflowRuns;

    public async Task<IReadOnlyList<IncidentOccurrence>> ReadAsync(IncidentQuery query, CancellationToken ct)
    {
        var since = query.Since;
        var rows = await session.Query<WebhookDelivery>()
            .Where(d => d.CreatedAt >= since
                && (d.Error != null || d.ResponseStatus == null || d.ResponseStatus < 200 || d.ResponseStatus >= 300))
            .OrderByDescending(d => d.CreatedAt)
            .Take(query.Limit)
            .ToListAsync(ct);

        // The response body is never read here. It can echo a credential, which is why reading it
        // needs its own capability on the deliveries list (#607).
        return rows.Select(d =>
        {
            var kind = d.ConnectorId is null ? "Webhook" : $"Connector {d.ConnectorSlug}";
            var outcome = d.ResponseStatus is { } status ? $"answered {status}" : "got no answer";
            return new IncidentOccurrence
            {
                Fingerprint = IncidentFingerprint.Of("delivery", kind, d.Url, d.ResponseStatus?.ToString(), IncidentFingerprint.Kind(d.Error)),
                Title = $"{kind} to {d.Url} {outcome}",
                Detail = d.Error,
                Tenant = query.Tenant,
                FirstSeenAt = d.CreatedAt,
                LastSeenAt = d.CreatedAt,
            };
        }).ToList();
    }
}
