using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Jobs.List;

internal sealed class ListJobsRequest : ListRequest
{
    /// <summary>One of <see cref="JobState"/>. Null = every state.</summary>
    public string? State { get; set; }
}

/// <summary>
/// A job as the API describes it. No command payload: a queued email carries an address and a body,
/// and a queued webhook carries whatever the request definition composed, so the payload is the one
/// field a read of the queue must not hand out.
/// </summary>
internal sealed class JobResponse
{
    public Guid Id { get; init; }
    public string State { get; init; } = nameof(JobState.Pending);
    public string CommandType { get; init; } = string.Empty;
    public string QueueId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime ExecuteAfter { get; init; }
    public DateTime ExpireOn { get; init; }
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? LastError { get; init; }

    public static JobResponse From(JobRecord r) => new()
    {
        Id = r.TrackingID,
        State = r.State.ToString(),
        CommandType = r.CommandType,
        QueueId = r.QueueID,
        CreatedAt = r.CreatedAt,
        ExecuteAfter = r.ExecuteAfter,
        ExpireOn = r.ExpireOn,
        AttemptCount = r.AttemptCount,
        MaxAttempts = r.MaxAttempts,
        NextAttemptAt = r.NextAttemptAt,
        CompletedAt = r.CompletedAt,
        LastError = r.LastError,
    };
}

/// <summary>GET /api/jobs, newest first, for the tenant of the request.</summary>
/// <remarks>
/// The session is the request's tenant-scoped one, so the filter that keeps one tenant's jobs from
/// another is Marten's, the same as every other list here. Nothing in this endpoint reaches across.
/// </remarks>
internal sealed class Endpoint : Endpoint<ListJobsRequest, PaginatedResponse<JobResponse>>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/jobs");
        Definition.RequireCapability(SystemCapabilities.ViewJobs);
    }

    public override async Task HandleAsync(ListJobsRequest req, CancellationToken ct)
    {
        var query = _session.Query<JobRecord>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.State))
        {
            if (!Enum.TryParse<JobState>(req.State, ignoreCase: true, out var state))
            {
                ThrowError($"State must be one of: {string.Join(", ", Enum.GetNames<JobState>())}.", 400);
                return;
            }

            query = query.Where(r => r.State == state);
        }

        var page = await query.OrderByDescending(r => r.CreatedAt).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<JobResponse>
        {
            Items = page.Items.Select(JobResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}
