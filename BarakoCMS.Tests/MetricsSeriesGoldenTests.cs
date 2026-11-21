using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using barakoCMS.Infrastructure.Security;

namespace BarakoCMS.Tests;

/// <summary>
/// The series names a single production-configured host publishes on /metrics. A host that keeps
/// prometheus-net's default registry must show the same names before and after the metrics moved
/// into the host's DI, the runtime, process and prometheus_net_metric_* series included.
/// </summary>
/// <remarks>
/// The fixture's hosts are isolated, which is what a production host is not, so this class puts the
/// default registry back for its one host. It does that by reflection and does nothing when the
/// type is absent, so the same file compiles and runs on a tree from before the isolation existed.
/// Run it alone: on a tree without isolation the default registry is shared by every host in the
/// process, and another class's families would show up here.
///
/// Compared by family name from the # TYPE lines, because a family is declared when its holder is
/// built, with or without a sample. Only the prefixes this change touches are compared:
/// <c>barakocms_</c>, the request, process and .NET collector families, and the
/// <c>prometheus_net_</c> ones. The event counter families (<c>system_runtime_*</c>) and the
/// <c>private_internaldiagnostics_*</c> ones appear when the runtime reports, not when the host
/// starts, so a name there is not stable between runs.
/// </remarks>
[Collection("Integration1")]
public class MetricsSeriesGoldenTests
{
    private const string ScrapeKey = "golden-scrape-key-3c1e"; // allow-secret

    private static readonly string[] Prefixes =
        ["barakocms_", "http_request", "process_", "dotnet_collection", "dotnet_total_memory", "prometheus_net_"];

    private static readonly string[] Expected =
    [
        "barakocms_api_key_refusals_total",
        "barakocms_auth_mail_dropped_total",
        "barakocms_jobs_attempts_total",
        "barakocms_jobs_dead_lettered",
        "barakocms_jobs_due",
        "barakocms_jobs_oldest_due_age_seconds",
        "barakocms_projection_lag_events",
        "barakocms_workflow_action_duration_seconds",
        "barakocms_workflow_attempts_claimed_total",
        "barakocms_workflow_attempts_total",
        "barakocms_workflow_backlog_measured_timestamp_seconds",
        "barakocms_workflow_due_runs",
        "barakocms_workflow_oldest_due_run_age_seconds",
        "barakocms_workflow_runner_last_pass_timestamp_seconds",
        "barakocms_workflow_runs_finished_total",
        "barakocms_workflow_runs_halted_total",
        "barakocms_workflow_runs_queued_total",
        "dotnet_collection_count_total",
        "dotnet_total_memory_bytes",
        "http_request_duration_seconds",
        "http_requests_in_progress",
        "http_requests_received_total",
        "process_cpu_seconds_total",
        "process_num_threads",
        "process_open_handles",
        "process_private_memory_bytes",
        "process_start_time_seconds",
        "process_virtual_memory_bytes",
        "process_working_set_bytes",
        "prometheus_net_eventcounteradapter_sources_connected_total",
        "prometheus_net_exemplars_recorded_total",
        "prometheus_net_meteradapter_instruments_connected",
        "prometheus_net_metric_families",
        "prometheus_net_metric_instances",
        "prometheus_net_metric_timeseries",
    ];

    private readonly IntegrationTestFixture _factory;

    public MetricsSeriesGoldenTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>The host as production builds it: the default registry, not the isolated one a test factory gives.</summary>
    private static void KeepTheDefaultRegistry(IServiceCollection services)
    {
        var type = typeof(Program).Assembly.GetType("barakoCMS.Infrastructure.Observability.HostMetrics");
        var make = type?.GetMethod("Default", BindingFlags.Public | BindingFlags.Static);
        if (type is null || make is null)
            return;

        services.Replace(ServiceDescriptor.Singleton(type, _ => make.Invoke(null, null)!));
    }

    /// <summary>
    /// Builds every holder, which is what makes its families exist. Holders that are DI services
    /// are resolved. Holders that were still static in a tree from before this change are touched
    /// through their class constructor. Whichever applies, the family exists afterwards.
    /// </summary>
    private static void BuildEveryHolder(IServiceProvider services)
    {
        Type[] holders =
        [
            typeof(barakoCMS.Features.Workflows.WorkflowMetrics),
            typeof(barakoCMS.Infrastructure.Jobs.JobMetrics),
            typeof(barakoCMS.Infrastructure.Auth.ApiKeyRefusals),
            typeof(barakoCMS.Infrastructure.Auth.AuthMailQueue),
            typeof(barakoCMS.Infrastructure.Health.WorkflowProjectionHealthCheck),
        ];

        foreach (var holder in holders)
        {
            services.GetService(holder);
            RuntimeHelpers.RunClassConstructor(holder.TypeHandle);
        }
    }

    [Fact]
    public async Task A_production_configured_host_publishes_exactly_these_series_names()
    {
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(MetricsScrapeAccess.ConfigurationKey, ScrapeKey);
            builder.ConfigureServices(KeepTheDefaultRegistry);
        });
        var client = host.CreateClient();
        (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        BuildEveryHolder(host.Services);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add(MetricsScrapeAccess.HeaderName, ScrapeKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var names = body.Split('\n')
            .Where(l => l.StartsWith("# TYPE ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[2])
            .Where(n => Prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        names.Should().NotBeEmpty();
        names.Should().Equal(Expected);
    }
}
