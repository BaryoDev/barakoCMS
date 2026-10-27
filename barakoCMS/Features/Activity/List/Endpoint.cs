using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Activity.List;

/// <summary>
/// GET /api/activity, one feed of what happened in the tenant: publishing, workflow runs, bounces,
/// sign-ins, imports and content type changes, merged from where each already lives.
/// </summary>
/// <remarks>
/// <para>
/// The route needs <c>view_audit_log</c>, since the feed reads the audit trail. Inside it each kind
/// still asks for what reads its source: runs need <c>view_workflow_runs</c>, bounces need
/// <c>manage_email_settings</c> and a registered <see cref="IMailBounceSource"/>, and an entry's rows
/// need the caller's read permission on that entry. A row the caller may not read is left out.
/// </para>
/// <para>
/// Every source is read for the caller's tenant, a SuperAdmin's included. The response carries no
/// event type name, stream id or event body, only the row shape.
/// </para>
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    ICallerCapabilities capabilities,
    IPermissionResolver permissions,
    IBranchScopeGuard branchGuard,
    ISensitivityService sensitivity,
    TenantContext tenant,
    IServiceProvider services,
    TimeProvider clock) : Endpoint<ListRequest, ActivityResponse>
{
    public override void Configure()
    {
        Get("/api/activity");
        Definition.RequireCapability(SystemCapabilities.ViewAuditLog, "SuperAdmin", "Admin");
        Description(b => b
            .Produces<ActivityResponse>(200)
            .Produces(400)
            .Produces(401)
            .Produces(403)
            .WithTags("Activity"));
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var limit = req.Take;
        var want = ActivityWant.Parse(req.Kinds);
        ActivityCursor? input = req.Since is not null && ActivityCursor.TryParse(req.Since, out var parsed) ? parsed : null;

        var slots = new List<ActivitySlot>();
        slots.AddRange(await ActivitySources.AuditAsync(session, tenant.Slug, want, input, limit, ct));

        if (want.HasAny(ActivityKinds.RunSucceeded, ActivityKinds.RunFailed)
            && await capabilities.HoldsAsync(User, SystemCapabilities.ViewWorkflowRuns, ct))
        {
            slots.AddRange(await ActivitySources.RunsAsync(session, want, input, limit, ct));
        }

        if (want.HasAny(ActivityKinds.Published, ActivityKinds.Scheduled, ActivityKinds.Edited)
            && Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            && await session.LoadAsync<User>(userId, ct) is { } caller)
        {
            slots.AddRange(await ActivitySources.EventsAsync(
                session, want, input, limit, now, (entry, token) => TitleIfReadableAsync(caller, entry, token), ct));
        }

        if (want.Has(ActivityKinds.Bounced)
            && services.GetService<IMailBounceSource>() is { } bounces
            && await capabilities.HoldsAsync(User, SystemCapabilities.ManageEmailSettings, ct))
        {
            slots.AddRange(await ActivitySources.BouncesAsync(bounces, tenant.Slug, input, limit, ct));
        }

        // A stable sort, so a source's own order holds inside one instant.
        var ordered = input is null
            ? slots.OrderByDescending(s => s.Position.At).ThenByDescending(s => s.Position.Source).ToList()
            : slots.OrderBy(s => s.Position.At).ThenBy(s => s.Position.Source).ToList();

        var hasMore = ordered.Count > limit;
        var page = ordered.Take(limit).ToList();

        await NameActorsAsync(page, ct);

        await Send.OkAsync(new ActivityResponse
        {
            Items = page.Where(s => s.Row is not null).Select(s => s.Row!).ToList(),
            HasMore = hasMore,
            Cursor = input is { } from
                ? ActivityCursor.Next(from, page.Count == 0 ? null : page[^1].Position, hasMore, now).ToString()
                : ActivityCursor.Start(now).ToString(),
        }, ct);
    }

    /// <summary>
    /// The title of an entry as this caller may see it, or null when they may not read the entry or
    /// sensitivity hides it whole. The rules run over a copy of the data, the same call the content
    /// endpoints make, so a title they would mask or drop is not shown here either.
    /// </summary>
    private async Task<string?> TitleIfReadableAsync(User caller, barakoCMS.Models.Content entry, CancellationToken ct)
    {
        if (!await branchGuard.MayAccessAsync(User, entry, ct)
            || !await permissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct))
        {
            return null;
        }

        var scrubbed = new Dictionary<string, object>(entry.Data);
        if (await sensitivity.ApplyAsync(entry, scrubbed, HttpContext, ct)) return null;
        return ActivitySources.TitleOf(entry.Data, scrubbed);
    }

    /// <summary>Gives the rows made by a user that user's name, in one read.</summary>
    private async Task NameActorsAsync(List<ActivitySlot> page, CancellationToken ct)
    {
        var ids = page.Where(s => s.Row is not null && s.ActorUserId is not null)
            .Select(s => s.ActorUserId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;

        var users = (await session.LoadManyAsync<User>(ct, ids)).ToDictionary(u => u.Id, u => u.Username);
        foreach (var slot in page.Where(s => s.Row is not null && s.ActorUserId is not null))
        {
            if (users.TryGetValue(slot.ActorUserId!.Value, out var name))
            {
                slot.Row!.Actor = new ActivityActor { Kind = "user", Name = ActivitySources.Clip(name) };
            }
        }
    }
}
