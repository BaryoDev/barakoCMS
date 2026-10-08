// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Collections.Concurrent;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #697: which core services a module can replace. The seams take the module's
/// implementation; the sealed ones keep core's and say so at startup.
/// </summary>
/// <remarks>
/// Every case goes through <c>AddBarakoCMS</c> with discovery off and one module added, because the
/// order that decides who wins is the order <c>AddBarakoCMS</c> registers in: modules first, core
/// after.
/// </remarks>
public class ModuleServiceReplacementTests
{
    private sealed class Replacing(Action<IServiceCollection> register) : IBarakoModule
    {
        public string Name => "Replacing";

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => register(services);
    }

    private static IServiceCollection Build(IBarakoModule module)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
            ["BarakoCMS:Modules:Enabled"] = "Replacing",
        }).Build();

        var services = new ServiceCollection();
        Host.AddBarakoCMS(services, config, m =>
        {
            m.Discover = false;
            m.Add(module);
        });
        return services;
    }

    [Fact]
    public void A_module_replaces_the_otp_service()
    {
        var fake = Substitute.For<IOtpService>();
        var services = Build(new Replacing(s => s.AddSingleton(fake)));

        Resolve<IOtpService>(services).Should().BeSameAs(fake);
    }

    [Fact]
    public void A_module_replaces_the_email_verification_service()
    {
        var fake = Substitute.For<IEmailVerificationService>();
        var services = Build(new Replacing(s => s.AddSingleton(fake)));

        Resolve<IEmailVerificationService>(services).Should().BeSameAs(fake);
    }

    [Fact]
    public void A_module_replaces_the_email_settings_provider()
    {
        var fake = Substitute.For<IEmailSettingsProvider>();
        var services = Build(new Replacing(s => s.AddSingleton(fake)));

        Resolve<IEmailSettingsProvider>(services).Should().BeSameAs(fake);
    }

    [Fact]
    public void Without_a_module_core_registers_its_own_seam_implementations()
    {
        var services = Build(new Replacing(_ => { }));

        Winner<IOtpService>(services).ImplementationType.Should().Be(typeof(OtpService));
        Winner<IEmailVerificationService>(services).ImplementationType.Should().Be(typeof(EmailVerificationService));
        Winner<IEmailSettingsProvider>(services).ImplementationType.Should().Be(typeof(EmailSettingsProvider));
    }

    public static TheoryData<Type> Sealed => new(SealedCoreServices.Types);

    [Theory]
    [MemberData(nameof(Sealed))]
    public void A_module_registering_a_sealed_service_is_warned_and_core_still_wins(Type sealedType)
    {
        var sink = new CollectingSink();
        ServiceDescriptor? theModules = null;
        IServiceCollection services;

        using (sink.Installed())
        {
            services = Build(new Replacing(s =>
            {
                theModules = ServiceDescriptor.Scoped(sealedType, _ => Substitute.For([sealedType], []));
                s.Add(theModules);
            }));
        }

        theModules.Should().NotBeNull("the module's ConfigureServices ran");
        var winner = services.Last(d => d.ServiceType == sealedType);
        winner.Should().NotBeSameAs(theModules, "core registers after the module, so core's is the one resolved");
        if (winner.ImplementationType is { } type)
        {
            type.Assembly.Should().BeSameAs(typeof(OtpService).Assembly);
        }

        var warnings = sink.Events
            .Where(e => e.MessageTemplate.Text == SealedCoreServices.Warning)
            .ToList();
        warnings.Should().HaveCount(1);
        warnings[0].Level.Should().Be(LogEventLevel.Warning);
        warnings[0].Properties["Module"].Should().Be(new ScalarValue("Replacing"));
        warnings[0].Properties["Service"].Should().Be(new ScalarValue(sealedType.FullName));
    }

    [Fact]
    public void Core_lists_the_four_sealed_services()
    {
        SealedCoreServices.Types.Should().HaveCount(4);
        SealedCoreServices.Types.Should().BeEquivalentTo(new[]
        {
            typeof(IContentWriter),
            typeof(ISensitivityService),
            typeof(IContentSourcingPolicy),
            typeof(ITemplateVariableExtractor),
        });
    }

    private static ServiceDescriptor Winner<T>(IServiceCollection services) =>
        services.Last(d => d.ServiceType == typeof(T));

    private static T Resolve<T>(IServiceCollection services) where T : notnull
    {
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Keeps what core logs during <c>AddBarakoCMS</c> inside this test's scope, the way
    /// <see cref="ModuleEnablementTests"/> does: Serilog's static logger is process-wide, so the
    /// sink keeps only events carrying a property this test pushed.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "ReplacementTest";
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
