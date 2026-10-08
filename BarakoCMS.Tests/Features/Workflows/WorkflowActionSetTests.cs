using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Built one by one because another action could not be built, an action keeps the lifetime it was
/// registered with: a singleton is built once and never disposed by the set, a scoped one is the
/// set's to dispose (#1111).
/// </summary>
public class WorkflowActionSetTests
{
    private sealed class DisposableAction(string type) : IWorkflowAction, IDisposable
    {
        public int Disposed { get; private set; }

        public string Type => type;

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) => Task.CompletedTask;

        public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            Task.FromResult(WorkflowActionResult.Success());

        public void Dispose() => Disposed++;
    }

    private sealed class SingletonAction() : DisposableActionBase("Singleton");

    private sealed class ScopedAction() : DisposableActionBase("Scoped");

    private abstract class DisposableActionBase(string type) : IWorkflowAction, IDisposable
    {
        public int Disposed { get; private set; }

        public string Type => type;

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) => Task.CompletedTask;

        public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            Task.FromResult(WorkflowActionResult.Success());

        public void Dispose() => Disposed++;
    }

    private static ServiceProvider Provider(out DisposableAction instance)
    {
        var services = new ServiceCollection();
        instance = new DisposableAction("Instance");
        services.AddSingleton<IWorkflowAction, SingletonAction>();
        services.AddScoped<IWorkflowAction, ScopedAction>();
        services.AddScoped<IWorkflowAction, BrokenConstructorAction>();
        services.AddSingleton<IWorkflowAction>(instance);
        services.AddSingleton(new WorkflowActionRegistrations(services));
        return services.BuildServiceProvider();
    }

    private static WorkflowActionSet Build(IServiceScope scope) =>
        WorkflowActionSet.Build(scope.ServiceProvider, NullLogger.Instance);

    [Fact]
    public async Task A_singleton_is_built_once_and_not_disposed_by_the_set()
    {
        using var provider = Provider(out _);

        IWorkflowAction first;
        using (var scope = provider.CreateScope())
        {
            await using var set = Build(scope);
            set.BuildFailures.Should().ContainSingle("only the broken action fails");
            first = set.Find("Singleton")!;
            first.Should().NotBeNull();
        }

        using (var scope = provider.CreateScope())
        {
            await using var set = Build(scope);
            set.Find("Singleton").Should().BeSameAs(first, "a singleton keeps its one instance and its state");
        }

        ((SingletonAction)first).Disposed.Should().Be(0, "the set does not own a singleton");
    }

    [Fact]
    public async Task A_scoped_action_is_disposed_with_the_set_and_a_registered_instance_is_not()
    {
        using var provider = Provider(out var instance);
        using var scope = provider.CreateScope();

        ScopedAction scoped;
        await using (var set = Build(scope))
        {
            set.Actions.Should().HaveCount(3, "every action but the broken one");
            scoped = (ScopedAction)set.Find("Scoped")!;
            set.Find("Instance").Should().BeSameAs(instance);
        }

        scoped.Disposed.Should().Be(1);
        instance.Disposed.Should().Be(0, "the set did not build it");
    }
}
