using barakoCMS.Extensions;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using FluentAssertions;
using NSubstitute;

namespace BarakoCMS.Tests;

/// <summary>
/// Module seeds run one per session and one per transaction.
///
/// They used to share a single session committed once at the end, so one module throwing lost every
/// module's seed, a module calling SaveChangesAsync itself committed everyone's half-finished work,
/// and any module could read and modify another's uncommitted data.
///
/// These use a fake session registered in DI rather than a database, because what is being asserted
/// is the runner's behaviour: how many sessions, how many commits, and what happens on failure.
/// </summary>
public class ModuleSeedIsolationTests
{
    private sealed class RecordingModule : IBarakoModule
    {
        private readonly Action? _onSeed;
        public RecordingModule(string name, Action? onSeed = null) { Name = name; _onSeed = onSeed; }
        public string Name { get; }
        public int SeedCalls { get; private set; }
        public object? SessionSeen { get; private set; }

        public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
        {
            SeedCalls++;
            SessionSeen = session;
            _onSeed?.Invoke();
            return Task.CompletedTask;
        }
    }

    // Substituted rather than hand-rolled: IDocumentSession is a large interface and none of it is
    // under test here. What is under test is the runner: how many sessions it creates, how many
    // times each is committed, and what it does when a module throws.
    private static IHost HostWith(params IBarakoModule[] modules) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                foreach (var m in modules) services.AddSingleton(m);
                // Scoped, exactly as the real registration is, so a new scope yields a new session.
                services.AddScoped<IDocumentSession>(_ => Substitute.For<IDocumentSession>());
            })
            .Build();

    private static int CommitsOn(object? session) =>
        ((IDocumentSession)session!).ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IDocumentSession.SaveChangesAsync));

    [Fact]
    public async Task Each_module_gets_its_own_session()
    {
        var a = new RecordingModule("A");
        var b = new RecordingModule("B");
        var host = HostWith(a, b);

        await host.RunBarakoModuleSeedersAsync();

        a.SeedCalls.Should().Be(1);
        b.SeedCalls.Should().Be(1);
        a.SessionSeen.Should().NotBeSameAs(b.SessionSeen,
            "a shared session lets one module read and modify another's uncommitted seed data");
    }

    [Fact]
    public async Task Each_module_is_committed_separately()
    {
        var a = new RecordingModule("A");
        var b = new RecordingModule("B");
        var host = HostWith(a, b);

        await host.RunBarakoModuleSeedersAsync();

        CommitsOn(a.SessionSeen).Should().Be(1);
        CommitsOn(b.SessionSeen).Should().Be(1,
            "one commit at the end for everyone means one failure discards every module's work");
    }

    [Fact]
    public async Task One_module_failing_does_not_stop_the_others()
    {
        var first = new RecordingModule("First");
        var boom = new RecordingModule("Boom", () => throw new InvalidOperationException("bad seed"));
        var last = new RecordingModule("Last");
        var host = HostWith(first, boom, last);

        var act = () => host.RunBarakoModuleSeedersAsync();

        (await act.Should().ThrowAsync<AggregateException>())
            .Which.InnerExceptions.Should().ContainSingle()
            .Which.Message.Should().Contain("Boom", "the failure must name the module");

        first.SeedCalls.Should().Be(1);
        last.SeedCalls.Should().Be(1, "a module after the failure must still get its turn");
        CommitsOn(first.SessionSeen).Should().Be(1, "its work must survive another module's failure");
        CommitsOn(last.SessionSeen).Should().Be(1);
    }

    [Fact]
    public async Task A_failing_module_does_not_commit()
    {
        var boom = new RecordingModule("Boom", () => throw new InvalidOperationException("bad seed"));
        var host = HostWith(boom);

        await host.Invoking(h => h.RunBarakoModuleSeedersAsync()).Should().ThrowAsync<AggregateException>();

        CommitsOn(boom.SessionSeen).Should().Be(0, "a half-applied seed is worse than none");
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_a_module_failure()
    {
        var a = new RecordingModule("A");
        var host = HostWith(a);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Shutting down is not a module's fault and must not be wrapped as one.
        await host.Invoking(h => h.RunBarakoModuleSeedersAsync(cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task No_modules_is_a_no_op()
    {
        var host = HostWith();
        await host.Invoking(h => h.RunBarakoModuleSeedersAsync()).Should().NotThrowAsync();
    }
}
