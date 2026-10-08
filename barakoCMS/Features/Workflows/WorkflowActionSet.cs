using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// The <see cref="IWorkflowAction"/> registrations, kept so an action can be built on its own when
/// building them all together fails.
/// </summary>
/// <remarks>
/// <para>
/// Holds the service collection and reads it on first use, after the host is built, so an action a
/// module or the host registers after the core is included.
/// </para>
/// <para>
/// A singleton built on its own is built from <paramref name="root"/>, never from a scope, and kept
/// here, so it is built once and keeps its state. Built from a scope it would hold that scope's
/// session after the scope was disposed. The container offers no way to ask for one registration's
/// instance, so this is a second instance beside any the container made; with one action that cannot
/// be built, the container's resolution of the list fails as a whole and its instances go unused.
/// </para>
/// </remarks>
internal sealed class WorkflowActionRegistrations(IServiceCollection services, IServiceProvider root)
{
    private readonly Lazy<ServiceDescriptor[]> _descriptors = new(() =>
        services.Where(d => d.ServiceType == typeof(IWorkflowAction) && !d.IsKeyedService).ToArray());

    private readonly ConcurrentDictionary<ServiceDescriptor, Lazy<IWorkflowAction>> _singletons = new();

    public IReadOnlyList<ServiceDescriptor> Descriptors => _descriptors.Value;

    /// <summary>
    /// The one instance of a singleton registration, built from the root provider. A build that
    /// throws is not kept, so the next call tries again.
    /// </summary>
    public IWorkflowAction Singleton(ServiceDescriptor descriptor, Func<IServiceProvider, IWorkflowAction> build)
    {
        var lazy = _singletons.GetOrAdd(descriptor, _ => new Lazy<IWorkflowAction>(() => build(root), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _singletons.TryRemove(new KeyValuePair<ServiceDescriptor, Lazy<IWorkflowAction>>(descriptor, lazy));
            throw;
        }
    }
}

/// <summary>
/// The workflow actions one scope can run, built so that one action whose constructor throws costs
/// only the steps that use it (#1111).
/// </summary>
/// <remarks>
/// <para>
/// Resolving <c>IEnumerable&lt;IWorkflowAction&gt;</c> builds every registered action, and one that
/// throws fails the whole resolution, so every step of every run failed with it, and so did saving,
/// validating and listing workflows. The container is asked first, as before, so a healthy host gets
/// the scope's own instances and their disposal. Only when that throws is each registration built on
/// its own, and the ones that throw are left out and named in <see cref="BuildFailures"/>.
/// </para>
/// <para>
/// Built that way, a scoped or transient action belongs to this set and is disposed with it. A
/// singleton is built once and kept on <see cref="WorkflowActionRegistrations"/>, and neither it nor
/// a registered instance is disposed here.
/// </para>
/// <para>
/// Only the exception's type is logged, never its message, which is whatever a module's constructor
/// put there and can be a setting or a connection detail. The describe endpoint logs it the same way.
/// </para>
/// </remarks>
internal sealed class WorkflowActionSet : IAsyncDisposable, IDisposable
{
    private readonly List<object> _owned;

    private WorkflowActionSet(IReadOnlyList<IWorkflowAction>? actions, IReadOnlyList<string> buildFailures, List<object> owned)
    {
        Actions = actions;
        BuildFailures = buildFailures;
        _owned = owned;
    }

    /// <summary>The actions that could be built, or null when none are registered at all.</summary>
    public IReadOnlyList<IWorkflowAction>? Actions { get; }

    /// <summary>Each registration that could not be built, as its class and the exception type.</summary>
    public IReadOnlyList<string> BuildFailures { get; }

    public IWorkflowAction? Find(string type) => Actions?.FirstOrDefault(a => a.Type == type);

    /// <summary>
    /// The error for a step whose action type is not among the built actions while some could not be
    /// built, since the missing one may be among them. Null when every action was built.
    /// </summary>
    public string? NotBuiltError(string type) =>
        BuildFailures.Count == 0
            ? null
            : $"No action of type '{type}' could be found, and {BuildFailures.Count} registered action(s) could not be built: "
            + string.Join(", ", BuildFailures) + ".";

    /// <summary>The set for a scope, as a container registration. The scope disposes it.</summary>
    public static WorkflowActionSet ForScope(IServiceProvider provider) =>
        Build(provider, provider.GetRequiredService<ILogger<WorkflowActionSet>>());

    public static WorkflowActionSet Build(IServiceProvider provider, ILogger logger)
    {
        try
        {
            var all = provider.GetService<IEnumerable<IWorkflowAction>>();
            return new WorkflowActionSet(all?.ToList(), [], []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Building the workflow actions together failed with {ExceptionType}, so each is built on its own",
                ex.GetType().Name);
        }

        if (provider.GetService<WorkflowActionRegistrations>() is not { } registrations)
            return new WorkflowActionSet([], ["the action registry (not available)"], []);

        var built = new List<IWorkflowAction>();
        var failures = new List<string>();
        var owned = new List<object>();

        foreach (var descriptor in registrations.Descriptors)
        {
            try
            {
                if (descriptor.ImplementationInstance is IWorkflowAction instance)
                {
                    built.Add(instance);
                }
                else if (descriptor.Lifetime == ServiceLifetime.Singleton)
                {
                    built.Add(registrations.Singleton(descriptor, root => Construct(root, descriptor)));
                }
                else
                {
                    var action = Construct(provider, descriptor);
                    owned.Add(action);
                    built.Add(action);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var name = descriptor.ImplementationType?.Name ?? "a factory registration";
                var cause = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                logger.LogError(
                    "Workflow action {Implementation} could not be built: {ExceptionType}. Steps that use it fail until it can be.",
                    name, cause.GetType().Name);
                failures.Add($"{name} ({cause.GetType().Name})");
            }
        }

        return new WorkflowActionSet(built, failures, owned);
    }

    private static IWorkflowAction Construct(IServiceProvider provider, ServiceDescriptor descriptor) =>
        (IWorkflowAction)(descriptor.ImplementationFactory is { } factory
            ? factory(provider)
            : ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _owned)
        {
            if (item is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (item is IDisposable disposable)
                disposable.Dispose();
        }

        _owned.Clear();
    }

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            if (item is IDisposable disposable)
                disposable.Dispose();
            else if (item is IAsyncDisposable asyncDisposable)
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _owned.Clear();
    }
}
