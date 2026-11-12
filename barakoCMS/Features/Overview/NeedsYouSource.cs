using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Health;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace barakoCMS.Features.Overview;

/// <summary>
/// The needs-you items for one tenant, with no request. It opens its own tenant session and scope,
/// reads counts and capped lists only, and writes nothing.
/// </summary>
internal sealed class NeedsYouSource(IServiceProvider services) : INeedsYouSource
{
    public async Task<IReadOnlyList<NeedsYouEntry>> ReadAsync(
        string tenant, IReadOnlySet<string> capabilities, CancellationToken cancellationToken = default)
    {
        var slug = TenantScopes.SlugFor(tenant);
        using var scope = services.CreateScopeForTenant(slug);
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IDocumentStore>();
        await using var session = slug == Models.Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(slug);

        var now = DateTimeOffset.UtcNow;
        var results = new List<RuleResult>();

        if (Holds(capabilities, SystemCapabilities.All))
        {
            var sources = await OverviewReads.AllContentAsync(session, cancellationToken);
            if (sources.Count > 0)
            {
                var (_, overdue) = await OverviewReads.CountDueAsync(sources, now, cancellationToken);
                results.Add(OverviewRules.OverduePublishes(overdue));
            }
        }

        if (Holds(capabilities, SystemCapabilities.ViewWorkflowRuns))
        {
            results.Add((await OverviewReads.FailedRunsAsync(session, now, cancellationToken)).Rule);
        }

        if (Holds(capabilities, SystemCapabilities.ManageEmailSettings)
            && sp.GetService<IMailBounceSource>() is { } bounces)
        {
            results.Add(OverviewRules.Bounces(await bounces.CountUnresolvedAsync(slug, cancellationToken)));
        }

        if (Holds(capabilities, SystemCapabilities.ViewMonitoring))
        {
            var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(
                r => r.Name == WorkflowProjectionHealthCheck.Name, cancellationToken);
            if (report.Entries.TryGetValue(WorkflowProjectionHealthCheck.Name, out var runner))
            {
                results.Add(OverviewRules.Runner(runner.Status));
            }
        }

        return OverviewRules.Order(results.Where(r => r.Item is not null).Select(r => r.Item!))
            .Select(i => new NeedsYouEntry(i.Id, i.Severity, i.Text, i.Ref.Kind, i.Ref.Id, i.Resolvable))
            .ToList();
    }

    private static bool Holds(IReadOnlySet<string> capabilities, string required) =>
        SystemCapabilities.Satisfies(capabilities, required);
}
