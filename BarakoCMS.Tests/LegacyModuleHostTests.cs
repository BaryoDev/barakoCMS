using System.Collections.Concurrent;
using System.Reflection;
using barakoCMS.Data;
using barakoCMS.Extensions;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Testcontainers.PostgreSql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host given two module assemblies built against older barakoCMS releases, with FastEndpoints'
/// own discovery left on, the way a deployment runs (#1010).
/// </summary>
/// <remarks>
/// <c>Barako.Fixture.Legacy42</c> was compiled against the BarakoCMS 4.2.0 package, so it names the
/// module contract and the models in assembly <c>barakoCMS</c>. <c>Barako.Fixture.Broken</c> names a
/// type no barakoCMS has. Both are built by <c>Fixtures/LegacyModules/build.sh</c>.
/// <para>
/// In the Sequential collection and on the fixture's JWT key, for the reasons
/// <see cref="EnabledModuleEndpointTests"/> gives.
/// </para>
/// </remarks>
[Collection("Sequential")]
public sealed class LegacyModuleHostTests(LegacyModuleHostTests.Host host) : IClassFixture<LegacyModuleHostTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("barako_legacy_modules")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        private readonly CollectingSink _sink = new();
        private WebApplication? _app;

        static Host() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        public WebApplication App => _app ?? throw new InvalidOperationException("The host has not started.");

        public Assembly Legacy { get; } = Fixture("Barako.Fixture.Legacy42.dll");

        public Assembly Broken { get; } = Fixture("Barako.Fixture.Broken.dll");

        public IReadOnlyCollection<LogEvent> Logged => _sink.Events;

        private static Assembly Fixture(string file) =>
            Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyModules", file));

        public async ValueTask InitializeAsync()
        {
            await _postgres.StartAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["DATABASE_URL"] = string.Empty,
                ["JWT:Key"] = IntegrationTestFixture.JwtKey,
                ["JWT:Issuer"] = "BarakoTest",
                ["JWT:Audience"] = "BarakoClient",
                ["InitialAdmin:Username"] = "admin",
                ["InitialAdmin:Password"] = $"Test-{Guid.NewGuid():N}!",
                ["Seed:DemoContent"] = "false",
                // Discovery on, so every first-party module this process references is seen and the
                // enabled list switches it off. Left undiscovered, one some other test loaded would
                // have its endpoints mapped with none of its services.
                ["BarakoCMS:Modules:Discover"] = "true",
                ["BarakoCMS:Modules:Enabled"] = "Legacy42Fixture",
            });

            using (_sink.Installed())
            {
                builder.Services.AddBarakoCMS(builder.Configuration, modules => modules.DiscoverFrom(Legacy, Broken));

                var app = builder.Build();
                app.UseBarakoCMS();
                await app.ApplyMartenSchemaAsync();
                await DataSeeder.SeedAsync(app);
                await app.RunBarakoModuleSeedersAsync();
                await app.StartAsync();
                _app = app;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
                await _app.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_module_built_against_4_2_is_registered_and_the_host_serves()
    {
        host.App.Services.GetServices<IBarakoModule>()
            .Should().Contain(m => m.Name == "Legacy42Fixture");

        var response = await host.App.GetTestClient().GetAsync("/health/build", TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_module_built_against_4_2_configures_its_services_with_the_moved_types()
    {
        var markerType = host.Legacy.GetType("Barako.Fixture.Legacy42.Legacy42Marker", throwOnError: true)!;

        var marker = host.App.Services.GetRequiredService(markerType);

        markerType.GetProperty("Status")!.GetValue(marker)!.ToString().Should().Be("Published");
        var page = markerType.GetProperty("Page")!.GetValue(marker)!;
        page.GetType().Assembly.GetName().Name.Should().Be("BarakoCMS.Abstractions");
    }

    [Fact]
    public void An_assembly_naming_a_type_no_barakoCMS_has_is_skipped_whole()
    {
        var catalogue = host.App.Services.GetRequiredService<ModuleCatalogue>();

        catalogue.Entries.Should().Contain(e => e.Name == "Legacy42Fixture",
            "the catalogue lists what discovery saw, so this is the control for the line below");
        catalogue.Entries.Should().NotContain(e => e.Name == "BrokenFixture",
            "its module type loads, but a type beside it does not, and the endpoint scan cannot use "
          + "the assembly, so registering the module would run it with none of its endpoints");
    }

    [Fact]
    public void An_assembly_naming_a_type_no_barakoCMS_has_is_named_in_a_warning()
    {
        var warnings = host.Logged
            .Where(e => e.Level == LogEventLevel.Warning)
            .Select(e => e.RenderMessage())
            .Where(m => m.Contains("Barako.Fixture.Broken", StringComparison.Ordinal))
            .ToList();

        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("barakoCMS.Models.NeverShipped");
    }

    /// <summary>
    /// Keeps only what this host logged while it started, by a property pushed through
    /// <see cref="LogContext"/>, since Serilog's static logger is shared with every test running.
    /// </summary>
    internal sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "LegacyModuleHost";
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue(Marker, out var value)
                && value is ScalarValue { Value: string id } && id == _id)
            {
                _events.Enqueue(logEvent);
            }
        }

        public IDisposable Installed()
        {
            var previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(this).CreateLogger();
            return new Restore(previous, LogContext.PushProperty(Marker, _id));
        }

        private sealed class Restore(Serilog.ILogger previous, IDisposable scope) : IDisposable
        {
            public void Dispose()
            {
                scope.Dispose();
                Log.Logger = previous;
            }
        }
    }
}
