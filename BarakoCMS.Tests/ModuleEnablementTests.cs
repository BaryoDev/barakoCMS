// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Collections.Concurrent;
using barakoCMS.Extensions;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>BarakoCMS:Modules:Enabled</c>, from issue #170: which of the modules a host holds actually run.
/// </summary>
/// <remarks>
/// Every case goes through <c>AddBarakoCMS</c> with discovery off, so the list under test is exactly
/// the two modules each test adds. What is asserted is what the host registered, because that is
/// what <c>GET /api/modules</c> and the seed runner read.
/// </remarks>
public class ModuleEnablementTests
{
    private sealed class Alpha : IBarakoModule
    {
        public string Name => "Alpha";
        public bool Seeded { get; private set; }
        public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            Seeded = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Bravo : IBarakoModule
    {
        public string Name => "Bravo";
        public bool Seeded { get; private set; }
        public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            Seeded = true;
            return Task.CompletedTask;
        }
    }

    private const string EnabledKey = "BarakoCMS:Modules:Enabled";

    private static IServiceCollection Build(
        IEnumerable<(string Key, string? Value)> settings, params IBarakoModule[] modules)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        foreach (var (key, value) in settings) pairs[key] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        var services = new ServiceCollection();
        Host.AddBarakoCMS(services, config, m =>
        {
            m.Discover = false;
            foreach (var module in modules) m.Add(module);
        });
        return services;
    }

    private static IReadOnlyList<string> Registered(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IBarakoModule))
            .Select(d => ((IBarakoModule)d.ImplementationInstance!).Name)
            .ToList();

    private static ModuleCatalogue Catalogue(IServiceCollection services) =>
        (ModuleCatalogue)services.Single(d => d.ServiceType == typeof(ModuleCatalogue)).ImplementationInstance!;

    /// <summary>
    /// The transition. An existing deployment has no list, and it must keep every module it had.
    /// </summary>
    [Fact]
    public void Unset_runs_every_module_and_warns_once_saying_how_to_set_the_list()
    {
        var sink = new CollectingSink();
        IServiceCollection services;
        using (sink.Installed())
        {
            services = Build([], new Alpha(), new Bravo());
        }

        Registered(services).Should().Equal("Alpha", "Bravo");

        var warnings = sink.Events
            .Where(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(EnabledKey))
            .ToList();
        warnings.Should().ContainSingle("one warning per boot, not one per module");
        var message = warnings[0].RenderMessage();
        message.Should().Contain("is not set");
        message.Should().Contain("Alpha, Bravo", "it says what is running unfiltered");
        message.Should().Contain("BarakoCMS__Modules__Enabled=Accounting,Files", "and how to set it");
        message.Should().Contain("empty string for core only");
    }

    [Fact]
    public void A_host_with_no_modules_at_all_gets_no_warning()
    {
        // Core only, by construction rather than by configuration: there is nothing the list would
        // decide, so a warning every boot would be noise.
        var sink = new CollectingSink();
        using (sink.Installed())
        {
            Build([]);
        }

        sink.Events.Should().NotContain(e => e.RenderMessage().Contains(EnabledKey));
    }

    [Fact]
    public void An_empty_string_means_core_only()
    {
        var services = Build([(EnabledKey, "")], new Alpha(), new Bravo());

        Registered(services).Should().BeEmpty();
        Catalogue(services).Entries.Should().HaveCount(2, "both were seen, neither runs");
        Catalogue(services).Entries.Should().OnlyContain(e => !e.Enabled);
    }

    [Fact]
    public void A_comma_separated_list_enables_exactly_the_named_modules_ignoring_case()
    {
        var services = Build([(EnabledKey, " bravo ")], new Alpha(), new Bravo());

        Registered(services).Should().Equal("Bravo");
        Catalogue(services).Entries.Should().BeEquivalentTo(new[]
        {
            new ModuleCatalogueEntry("Alpha", 0, false),
            new ModuleCatalogueEntry("Bravo", 0, true),
        });
    }

    [Fact]
    public void An_array_is_read_the_same_way()
    {
        // The JSON provider flattens ["Alpha"] to Enabled:0 = Alpha.
        var services = Build([(EnabledKey + ":0", "Alpha")], new Alpha(), new Bravo());

        Registered(services).Should().Equal("Alpha");
    }

    [Fact]
    public void A_name_that_matches_nothing_refuses_startup_and_lists_what_is_available()
    {
        var act = () => Build([(EnabledKey, "Alpha,Accounting")], new Alpha(), new Bravo());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Accounting'*", "the offending name, so a typo is visible")
            .WithMessage("*Alpha, Bravo*", "and the names that would have worked")
            .WithMessage($"*{EnabledKey}*", "and the key to fix");
    }

    /// <summary>
    /// A module enabled for the first time seeds on that boot, because the seed runner reads the
    /// same registrations the list filtered, and a disabled one is never asked.
    /// </summary>
    [Fact]
    public async Task Only_an_enabled_module_is_seeded()
    {
        var alpha = new Alpha();
        var bravo = new Bravo();
        var services = Build([(EnabledKey, "Alpha")], alpha, bravo);

        // The runner opens a session per module; a substitute stands in for the store.
        services.RemoveAll<IDocumentSession>();
        services.AddScoped(_ => Substitute.For<IDocumentSession>());
        await using var provider = services.BuildServiceProvider();

        await provider.RunBarakoModuleSeedersAsync(TestContext.Current.CancellationToken);

        alpha.Seeded.Should().BeTrue();
        bravo.Seeded.Should().BeFalse("it was not enabled, so nothing should have asked it to seed");
    }

    /// <summary>
    /// Captures what core logs during <c>AddBarakoCMS</c>, which writes to Serilog's static logger.
    /// </summary>
    /// <remarks>
    /// That logger is process-wide and other test classes call <c>AddBarakoCMS</c> in parallel, so
    /// the sink keeps only events raised inside <see cref="Installed"/>'s scope: a property pushed
    /// through <see cref="LogContext"/>, which is async-local and so follows this test alone.
    /// </remarks>
    private sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "EnablementTest";
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
