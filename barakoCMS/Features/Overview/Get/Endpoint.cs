using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Content.Counts;
using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WorkflowHealth = barakoCMS.Features.Workflows.GetWorkflowHealth;

namespace barakoCMS.Features.Overview.Get;

/// <summary>
/// GET /api/overview, the rule-based "Today at a glance": counts with a prior period, the things that
/// need a person, and the health strip, from one place so every client agrees.
/// </summary>
/// <remarks>
/// <para>
/// Any signed-in caller may ask. Each part is left null for a caller who lacks what reads it, using
/// the same capabilities the routes behind it ask for: <c>view_workflow_runs</c> for the workflow
/// part, <c>view_monitoring</c> for the health strip, <c>view_audit_log</c> for sign-ins,
/// <c>manage_email_settings</c> for bounces, and content read for the publish counts. The verdict
/// is the worst of the parts the caller sees, so two callers can see different verdicts.
/// </para>
/// <para>
/// Everything is a count or a capped list read for the caller's tenant, never a loaded document set.
/// The rules are in <see cref="OverviewRules"/>. The route is open to a <c>monitor:read</c> key.
/// </para>
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    ICallerCapabilities capabilities,
    IPermissionResolver permissionResolver,
    IBranchScopeGuard branchGuard,
    HealthCheckService healthCheckService,
    TenantContext tenant,
    IServiceProvider services)
    : EndpointWithoutRequest<OverviewResponse>
{
    public override void Configure()
    {
        Get("/api/overview"); // authenticated by default, each part checks its own capability
        Description(b => b
            .Produces<OverviewResponse>(200)
            .Produces(401)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var results = new List<RuleResult>();

        var mayRuns = await capabilities.HoldsAsync(User, SystemCapabilities.ViewWorkflowRuns, ct);
        var mayMonitor = await capabilities.HoldsAsync(User, SystemCapabilities.ViewMonitoring, ct);
        var mayAudit = await capabilities.HoldsAsync(User, SystemCapabilities.ViewAuditLog, ct);
        var mayMail = await capabilities.HoldsAsync(User, SystemCapabilities.ManageEmailSettings, ct);

        var bounces = services.GetService<IMailBounceSource>();
        var unresolved = bounces is null ? 0 : await bounces.CountUnresolvedAsync(tenant.Slug, ct);

        PeriodCount? publishesDue;
        PeriodCount? failedRuns = null;
        PeriodCount? bounceCount = null;
        PeriodCount? signIns = null;
        WorkflowOverview? workflows = null;
        HealthStripResponse? health = null;

        publishesDue = await ReadContentAsync(now, results, ct);

        if (mayRuns)
        {
            (failedRuns, workflows) = await ReadWorkflowsAsync(now, results, ct);
        }

        if (mayAudit)
        {
            signIns = await SignInsAsync(now, ct);
        }

        if (mayMail && bounces is not null)
        {
            bounceCount = new PeriodCount
            {
                Current = await bounces.CountAsync(tenant.Slug, now.AddHours(-OverviewRules.PeriodHours), now, ct),
                Prior = await bounces.CountAsync(
                    tenant.Slug, now.AddHours(-2 * OverviewRules.PeriodHours), now.AddHours(-OverviewRules.PeriodHours), ct),
            };
            results.Add(OverviewRules.Bounces(unresolved));
        }

        if (mayMonitor)
        {
            health = await ReadHealthAsync(bounces is not null ? unresolved : null, results, ct);
        }

        var items = OverviewRules.Order(results.Where(r => r.Item is not null).Select(r => r.Item!));

        await Send.OkAsync(new OverviewResponse
        {
            Verdict = OverviewRules.Verdict(results.Select(r => r.Status)),
            Headline = OverviewRules.Headline(results),
            GeneratedAt = now,
            Counts = new OverviewCounts
            {
                PublishesDue = publishesDue,
                FailedRuns = failedRuns,
                Bounces = bounceCount,
                SignIns = signIns,
            },
            Workflows = workflows,
            Health = health,
            NeedsYou = items,
        }, ct);
    }

    private async Task<PeriodCount?> ReadContentAsync(DateTimeOffset now, List<RuleResult> results, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            return null;
        }

        var reach = await ReachableContent.ForAsync(session, permissionResolver, branchGuard, User, user, null, ct);
        if (reach.Sources.Count == 0) return null;

        var horizon = now.UtcDateTime.AddHours(OverviewRules.PeriodHours);
        var overdueBefore = now.UtcDateTime.AddMinutes(-OverviewRules.OverdueGraceMinutes);
        var due = 0;
        var overdue = 0;
        foreach (var source in reach.Sources)
        {
            due += await source
                .Where(c => c.Status == ContentStatus.Scheduled && c.ScheduledPublishAt <= horizon)
                .CountAsync(ct);
            overdue += await source
                .Where(c => c.Status == ContentStatus.Scheduled && c.ScheduledPublishAt < overdueBefore)
                .CountAsync(ct);
        }

        results.Add(OverviewRules.OverduePublishes(overdue));
        return new PeriodCount { Current = due, Prior = null };
    }

    private async Task<(PeriodCount Failed, WorkflowOverview Workflows)> ReadWorkflowsAsync(
        DateTimeOffset now, List<RuleResult> results, CancellationToken ct)
    {
        var day = await WorkflowHealth.Endpoint.DayHealthAsync(session, now, ct);

        var dayCounts = await RunQueries.CountSinceByWorkflowAsync(
            session, now.AddHours(-OverviewRules.PeriodHours), WorkflowHealth.WorkflowHealthRules.CountedStatuses, ct);
        var failedDay = dayCounts.Where(c => c.Key.Status != RunStatus.Succeeded).Sum(c => c.Value);
        var finished = dayCounts.Values.Sum();

        var twoDays = await RunQueries.CountSinceByWorkflowAsync(
            session, now.AddHours(-2 * OverviewRules.PeriodHours), [RunStatus.Failed, RunStatus.PartiallyFailed], ct);

        results.Add(OverviewRules.FailedRuns(day.Verdict, failedDay, finished));

        return (
            new PeriodCount { Current = failedDay, Prior = twoDays.Values.Sum() - failedDay },
            new WorkflowOverview { Runs24h = day.Runs24h, SuccessRate = day.SuccessRate, Verdict = day.Verdict, Summary = day.Summary });
    }

    private async Task<PeriodCount> SignInsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // AuditEvent is one table for the deployment, so the tenant is filtered here and not by the session.
        var slug = tenant.Slug;
        var from = now.UtcDateTime.AddHours(-OverviewRules.PeriodHours);
        var before = now.UtcDateTime.AddHours(-2 * OverviewRules.PeriodHours);
        var current = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug && e.Action == "auth.login.succeeded" && e.CreatedAt >= from)
            .CountAsync(ct);
        var prior = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug && e.Action == "auth.login.succeeded" && e.CreatedAt >= before && e.CreatedAt < from)
            .CountAsync(ct);
        return new PeriodCount { Current = current, Prior = prior };
    }

    private async Task<HealthStripResponse> ReadHealthAsync(int? unresolvedBounces, List<RuleResult> results, CancellationToken ct)
    {
        var report = await healthCheckService.CheckHealthAsync(ct);
        var strip = new List<RuleResult> { OverviewRules.Api(Monitoring.Meta.Endpoint.ReadVersion()) };

        if (report.Entries.TryGetValue("Database", out var database))
            strip.Add(OverviewRules.Database(database.Status, (long)database.Duration.TotalMilliseconds));
        if (report.Entries.TryGetValue("Disk Space", out var disk))
            strip.Add(OverviewRules.Storage(disk.Status));
        if (unresolvedBounces is { } waiting)
            strip.Add(OverviewRules.Mail(waiting));
        if (report.Entries.TryGetValue(barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck.Name, out var runner))
            strip.Add(OverviewRules.Runner(runner.Status));

        results.AddRange(strip);
        return new HealthStripResponse
        {
            Verdict = OverviewRules.Verdict(strip.Select(r => r.Status)),
            Items = strip.Select(r => r.Strip!).ToList(),
        };
    }
}
