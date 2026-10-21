using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Runs;

/// <summary>
/// GET /api/contents/{id}/runs, the workflow runs that fired for an entry, newest first.
/// </summary>
/// <remarks>
/// <para>
/// With <c>includeCreated=true</c> it also lists the runs whose actions created the entry, so a
/// screen on a created entry can say which run made it.
/// </para>
/// <para>
/// An id that is not a Guid is a 400. A caller who cannot read the entry gets the 404 an unknown id gets. A 403 here would tell them
/// the entry exists. The read check is the one the entry endpoint makes, so it covers branch rules
/// too. The capability gate runs first, so a caller without
/// <see cref="SystemCapabilities.ViewWorkflowRuns"/> gets 403 whatever the id.
/// </para>
/// </remarks>
internal sealed class Endpoint(IQuerySession session, IPermissionResolver permissionResolver)
    : EndpointWithoutRequest<RunsResponse>
{
    private const int Limit = 100;

    // Candidates read before the filter on what an action created. Bounded so an id that shows up in
    // a great many runs cannot make this read them all.
    private const int CandidateLimit = 1000;

    public override void Configure()
    {
        Get("/api/contents/{id}/runs");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, RunGate.LegacyRoles);
        Description(b => b
            .Produces<RunsResponse>(200)
            .Produces(401)
            .Produces(403)
            .Produces(404)
            .WithTags("Content"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("id"), out var id))
            ThrowError("That is not an entry id.", 400);

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<Models.User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var content = await session.LoadAsync<Models.Content>(id, ct);
        if (content is null
            || !await permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var includeCreated = Query<bool>("includeCreated", isRequired: false);
        var runs = includeCreated ? await RunsTouchingAsync(id, ct) : await RunsOfAsync(id, ct);

        await Send.OkAsync(new RunsResponse(runs.Select(Summarise).ToList()), ct);
    }

    private async Task<IReadOnlyList<WorkflowRun>> RunsOfAsync(Guid id, CancellationToken ct) =>
        await session.Query<WorkflowRun>()
            .Where(r => r.ContentId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Limit)
            .ToListAsync(ct);

    /// <summary>The runs of the entry and the runs whose actions created it.</summary>
    /// <remarks>
    /// The id is looked for as text in the run document, then confirmed against each action's
    /// <c>Created</c> list in memory. A nested <c>Any</c> over the actions and then their created
    /// entries is not something to rely on Marten translating, and the text match keeps the SQL to
    /// one simple filter. The match can only over-select, and the confirmation drops those.
    /// </remarks>
    private async Task<IReadOnlyList<WorkflowRun>> RunsTouchingAsync(Guid id, CancellationToken ct)
    {
        var candidates = await session.Query<WorkflowRun>()
            .Where(r => r.ContentId == id || r.MatchesSql("d.data::text like ?", $"%{id}%"))
            .OrderByDescending(r => r.CreatedAt)
            .Take(CandidateLimit)
            .ToListAsync(ct);

        return candidates
            .Where(r => r.ContentId == id
                || r.Actions.Any(a => a.Created?.Any(c => c.Id == id) == true))
            .Take(Limit)
            .ToList();
    }

    private static RunSummary Summarise(WorkflowRun run) => new(
        run.Id,
        run.WorkflowName,
        run.TriggerEvent,
        run.Status.ToString(),
        run.CreatedAt,
        run.CompletedAt,
        run.Actions.OrderBy(a => a.Ordinal)
            .Select(a => new RunActionSummary(a.Ordinal, a.ActionType, a.Status.ToString(), a.Error, a.Created))
            .ToList());
}
