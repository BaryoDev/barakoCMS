// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Collections.Concurrent;
using barakoCMS.Extensions;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The schema preflight from issue #519: before anything is applied, the host works out what each
/// module wants from the database and refuses by name when a CreateOnly store would refuse it.
/// </summary>
/// <remarks>
/// Each case builds its own container through <c>AddBarakoCMS</c> with discovery off and one
/// probe module, against the shared fixture database, and calls the preflight directly. Nothing
/// here applies schema, so the shared database is read and never changed. The probes are private
/// nested types, which discovery ignores, so no other host in the suite picks them up.
/// </remarks>
[Collection("Sequential")]
public class ModuleSchemaPreflightTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleSchemaPreflightTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// The case the issue is about: a module reaching a core table through the obsolete hook, which
    /// the ownership check on ConfigureSchema does not cover.
    /// </summary>
    private sealed class CoreTableProbe : IBarakoModule
    {
        public const string ModuleName = "Core Table Probe";
        public string Name => ModuleName;

#pragma warning disable CS0618 // the deprecated hook is the path under test
        public void ConfigureMarten(StoreOptions options) =>
            options.Schema.For<Content>().Index(x => x.LastModifiedBy);
#pragma warning restore CS0618
    }

    private sealed class OwnDocumentsProbe : IBarakoModule
    {
        public const string ModuleName = "Own Documents Probe";
        public const string TableAlias = "preflight_probe_documents";
        public string Name => ModuleName;

        public void ConfigureSchema(IModuleSchema schema) =>
            schema.For<PreflightProbeDocument>().DocumentAlias(TableAlias).Index(x => x.Label);
    }

    public sealed class PreflightProbeDocument
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    private ServiceProvider Build(IBarakoModule module, AutoCreate autoCreate, string? preflight = null)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _factory.ConnectionString,
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        if (preflight is not null)
            pairs[ModuleSchemaPreflight.EnabledKey] = preflight;

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        Host.AddBarakoCMS(services, config, m =>
        {
            m.Discover = false;
            m.Add(module);
        });

        // The fixture sets ASPNETCORE_ENVIRONMENT=Development for the process, which picks
        // CreateOrUpdate inside AddBarakoCMS; IConfigureMarten runs after that and wins.
        services.ConfigureMarten(opts => opts.AutoCreateSchemaObjects = autoCreate);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The probe adds an index to the contents table, so that table has to exist first. Other
    /// tests create content and would usually have done it, but order is not something to rely on.
    /// </summary>
    private async Task EnsureContentsTableExists()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(Content), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_module_that_changes_an_existing_core_table_is_refused_under_CreateOnly_and_named()
    {
        await EnsureContentsTableExists();
        await using var provider = Build(new CoreTableProbe(), AutoCreate.CreateOnly);

        var act = () => provider.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken);

        var refusal = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        refusal.Should().Contain(CoreTableProbe.ModuleName, "the operator needs to know which module to look at");
        refusal.Should().Contain("mt_doc_contents", "and which object it wants to change");
        refusal.Should().Contain("AutoCreate.CreateOnly", "and which policy refused it");
        refusal.Should().Contain("AutoCreate.CreateOrUpdate", "and what would allow it");
        refusal.Should().Contain(ModuleSchemaPreflight.EnabledKey, "and how to switch the check off");
        refusal.Should().Contain("ConfigureMarten", "the object is core's, so the module is named for the hook that can reach it");
    }

    [Fact]
    public async Task The_same_module_passes_under_AutoCreate_All()
    {
        await EnsureContentsTableExists();
        await using var provider = Build(new CoreTableProbe(), AutoCreate.All);

        await provider.Invoking(p => p.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync("All applies every delta, so nothing is refused");

        // Off by default on a store that would apply the change anyway, so nothing was computed.
        provider.GetRequiredService<ModuleSchemaReport>().Computed.Should().BeFalse();
    }

    /// <summary>
    /// The endpoint's needs-migration state is only ever visible here: a CreateOnly store refuses
    /// to boot instead, so a developer runs the check on a store that applies the change and reads
    /// off what production would have refused.
    /// </summary>
    [Fact]
    public async Task Switched_on_under_All_the_change_is_reported_against_the_module_rather_than_refused()
    {
        await EnsureContentsTableExists();
        await using var provider = Build(new CoreTableProbe(), AutoCreate.All, preflight: "true");

        await provider.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken);

        var report = provider.GetRequiredService<ModuleSchemaReport>();
        report.Computed.Should().BeTrue();
        var finding = report.For(CoreTableProbe.ModuleName);
        finding.Should().NotBeNull();
        finding!.State.Should().Be(ModuleSchemaState.NeedsMigration);
        finding.Changes.Should().ContainSingle()
            .Which.Should().Match<ModuleSchemaChange>(c => c.Name.EndsWith("mt_doc_contents") && c.ReachedThroughConfigureMarten);
        report.For(ModuleSchemaPreflight.CoreName)!.Changes.Should().BeEmpty(
            "a change reached through ConfigureMarten is the module's, not core's");
    }

    [Fact]
    public async Task A_module_with_only_new_documents_passes_under_CreateOnly()
    {
        await using var provider = Build(new OwnDocumentsProbe(), AutoCreate.CreateOnly);

        await provider.Invoking(p => p.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync("CreateOnly creates a missing table with its indexes");

        var finding = provider.GetRequiredService<ModuleSchemaReport>().For(OwnDocumentsProbe.ModuleName);
        finding.Should().NotBeNull();
        finding!.State.Should().Be(ModuleSchemaState.Ready);
        finding.NewObjects.Should().ContainSingle(n => n.EndsWith(OwnDocumentsProbe.TableAlias));
        finding.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_flag_off_lets_the_change_through_to_Marten_regardless()
    {
        await EnsureContentsTableExists();
        await using var provider = Build(new CoreTableProbe(), AutoCreate.CreateOnly, preflight: "false");

        await provider.Invoking(p => p.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken))
            .Should().NotThrowAsync("false keeps today's behaviour, where Marten refuses on apply");

        provider.GetRequiredService<ModuleSchemaReport>().Computed.Should().BeFalse();
    }

    [Fact]
    public async Task It_logs_one_line_per_module_naming_what_it_wants()
    {
        await using var provider = Build(new OwnDocumentsProbe(), AutoCreate.CreateOnly);
        var sink = new CollectingSink();

        using (sink.Installed())
        {
            await provider.PreflightModuleSchemaAsync(TestContext.Current.CancellationToken);
        }

        var lines = sink.Events.Where(e => e.Level == LogEventLevel.Information).Select(e => e.RenderMessage()).ToList();
        lines.Should().ContainSingle(l => l.Contains(OwnDocumentsProbe.ModuleName))
            .Which.Should().Contain(OwnDocumentsProbe.TableAlias);
        // Serilog quotes string properties when it renders, so match on the pieces.
        lines.Should().ContainSingle(l => l.Contains("\"core\" schema:"), "core gets its own line");
    }

    /// <summary>
    /// Serilog's static logger is process-wide and other collections log through it, so this only
    /// keeps events carrying a marker pushed for the duration of the test.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "PreflightTest";
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
