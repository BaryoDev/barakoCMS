// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Collections.Concurrent;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
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
/// A host that set the key of the email module that left this repository (#1216) upgrades onto the
/// mock service. The log has to say so.
/// </summary>
[Collection(nameof(GlobalLoggerCollection))]
public class LegacyEmailKeyWarningTests
{
    private static List<LogEvent> Boot(string? legacyKey, bool providerRegistered = false)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        if (legacyKey is not null) pairs["Resend:ApiKey"] = legacyKey;

        var services = new ServiceCollection();
        if (providerRegistered) services.AddScoped(_ => Substitute.For<IEmailService>());

        var sink = new CollectingSink();
        using (sink.Installed())
        {
            Host.AddBarakoCMS(services, new ConfigurationBuilder().AddInMemoryCollection(pairs).Build(),
                m => m.Discover = false);
        }
        return sink.Events.ToList();
    }

    [Fact]
    public void An_old_provider_key_with_only_the_mock_registered_logs_a_warning_naming_the_docs()
    {
        var events = Boot("re_old_key");

        var warning = events.Should().ContainSingle(e => e.Level == LogEventLevel.Warning
            && e.MessageTemplate.Text == LegacyEmailKeyCheck.Warning).Subject;
        warning.RenderMessage().Should().Contain("docs/configuring-email.md").And.Contain("left this repository");
    }

    [Fact]
    public void No_old_key_means_no_warning()
    {
        Boot(null).Should().NotContain(e => e.MessageTemplate.Text == LegacyEmailKeyCheck.Warning);
    }

    [Fact]
    public void An_old_key_next_to_a_registered_provider_means_no_warning()
    {
        Boot("re_old_key", providerRegistered: true)
            .Should().NotContain(e => e.MessageTemplate.Text == LegacyEmailKeyCheck.Warning);
    }

    /// <summary>
    /// The Serilog logger is process-wide and other classes call <c>AddBarakoCMS</c> in parallel, so
    /// only events raised inside <see cref="Installed"/>'s async-local scope are kept.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "LegacyEmailKeyTest";
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
