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
/// Compared by family from the # TYPE and # HELP lines, because a family is declared when its holder
/// is built, with or without a sample. The golden file is
/// Fixtures/MetricsGolden/barakocms-metrics.golden.tsv, taken from 2d1482f0. It marks each family
/// startup or first-use. Only the prefixes this change touches are compared:
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

    private sealed record Family(string Name, string Type, string Labels, string When, string Help);

    /// <summary>The golden file. Its values come from the Create* calls at 2d1482f0, not from this tree.</summary>
    private static List<Family> Golden()
    {
        using var stream = typeof(MetricsSeriesGoldenTests).Assembly.GetManifestResourceStream("barakocms-metrics.golden.tsv")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n')
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .Select(c => new Family(c[0], c[1], c[2], c[3], c[4]))
            .ToList();
    }

    /// <summary>Name, TYPE, HELP and label names of every barakocms_ family the default registry holds.</summary>
    private static List<Family> Scraped(string body)
    {
        var help = new Dictionary<string, string>();
        var type = new Dictionary<string, string>();
        foreach (var line in body.Split('\n'))
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 3 || parts[0] != "#") continue;
            if (parts[1] == "HELP") help[parts[2]] = parts.Length > 3 ? parts[3] : "";
            if (parts[1] == "TYPE") type[parts[2]] = parts.Length > 3 ? parts[3] : "";
        }

        // A family with no child shows no sample, so its label names come from its collector.
        // prometheus-net 8.2.1 keeps a CollectorFamily per name in _families, and each family
        // keeps its collectors in _collectors.
        var families = (System.Collections.IDictionary)typeof(Prometheus.CollectorRegistry)
            .GetField("_families", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Prometheus.Metrics.DefaultRegistry)!;

        string LabelNames(string name)
        {
            var family = families[name]!;
            var collectors = (System.Collections.IDictionary)family.GetType()
                .GetField("_collectors", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(family)!;
            return string.Join(';', collectors.Values.Cast<Prometheus.Collector>()
                .Select(c => string.Join(',', c.LabelNames)));
        }

        return type.Keys
            .Where(n => n.StartsWith("barakocms_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => new Family(n, type[n], LabelNames(n), "", help.GetValueOrDefault(n, "")))
            .ToList();
    }

    private static List<string> LibraryNames(string body) =>
        body.Split('\n')
            .Where(l => l.StartsWith("# TYPE ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[2])
            .Where(n => Prefixes.Where(p => p != "barakocms_").Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

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

    private static async Task<string> ScrapeAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add(MetricsScrapeAccess.HeaderName, ScrapeKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stage one scrapes right after startup, before any other request: the startup families and
    /// the library ones must be there, and a first-use one may be too. Stage two builds every
    /// holder, which is what first use does, and every barakocms_ family must then match the golden
    /// file in name, TYPE, HELP and label names.
    /// </summary>
    [Fact]
    public async Task A_production_configured_host_publishes_the_golden_series()
    {
        var golden = Golden();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(MetricsScrapeAccess.ConfigurationKey, ScrapeKey);
            builder.ConfigureServices(KeepTheDefaultRegistry);
        });
        var client = host.CreateClient();

        // A first-use family may appear sooner (a holder built by DI creates its family when it is
        // built), which a scraper does not mind. A startup family that is missing is a regression.
        var atStartup = await ScrapeAsync(client);
        Scraped(atStartup).Select(f => f.Name).Should().Contain(
            golden.Where(g => g.When == "startup").Select(g => g.Name),
            "master creates these families while the host starts");
        LibraryNames(atStartup).Should().Equal(golden.Where(g => g.When == "lib").Select(g => g.Name));

        BuildEveryHolder(host.Services);
        var afterUse = await ScrapeAsync(client);
        Scraped(afterUse).Should().BeEquivalentTo(
            golden.Where(g => g.When != "lib").Select(g => g with { When = "" }).OrderBy(g => g.Name, StringComparer.Ordinal),
            o => o.WithStrictOrdering());
        LibraryNames(afterUse).Should().Equal(golden.Where(g => g.When == "lib").Select(g => g.Name));
    }
}
