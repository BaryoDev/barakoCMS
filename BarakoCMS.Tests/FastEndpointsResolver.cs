using System.Reflection;
using FastEndpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Puts FastEndpoints' process-wide service resolver back after a test builds and disposes a host of
/// its own (#554).
/// </summary>
/// <remarks>
/// <para>
/// <c>UseFastEndpoints</c> points a static resolver at the provider of the host that called it. Once
/// that host is disposed the static still points at the disposed provider, and the next code in the
/// process that goes through it outside a request fails with <see cref="ObjectDisposedException"/>.
/// <c>JwtBearer.CreateToken</c> is one: it reads its default options through the resolver, so every
/// token the core issues after such a test fails.
/// </para>
/// <para>
/// A test that builds its own host takes <see cref="Keep"/> before it builds, and declares it before
/// the host so the host is disposed first. FastEndpoints 8.3 keeps the resolver internal, so this
/// reaches it by reflection and throws on first use if that shape changes rather than restoring
/// nothing.
/// </para>
/// </remarks>
internal static class FastEndpointsResolver
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Type Resolver =
        typeof(IServiceResolver).Assembly.GetType("FastEndpoints.ServiceResolver")
        ?? throw Moved("the type FastEndpoints.ServiceResolver");

    private static readonly PropertyInfo Instance =
        Resolver.GetProperty("Instance", AnyStatic) ?? throw Moved("ServiceResolver.Instance");

    private static readonly PropertyInfo InstanceNotSet =
        Resolver.GetProperty("InstanceNotSet", AnyStatic) ?? throw Moved("ServiceResolver.InstanceNotSet");

    /// <summary>
    /// Held around every read-then-write of the static, so a host disposed on one thread is never
    /// written back by another. <see cref="Lock"/> is reentrant, so a holder may call back in.
    /// </summary>
    private static readonly Lock Gate = new();

    private static readonly List<Func<IEnumerable<IServiceProvider>>> Fallbacks = [];

    private static readonly System.Collections.IDictionary JobQueues =
        (System.Collections.IDictionary?)typeof(JobQueueExtensions).Assembly.GetType("FastEndpoints.JobQueueBase")?
            .GetField("JobQueues", AnyStatic)?.GetValue(null)
        ?? throw Moved("JobQueueBase.JobQueues");

    private static readonly Type CommandHandlerRegistry =
        typeof(CommandExtensions).Assembly.GetType("FastEndpoints.CommandHandlerRegistry")
        ?? throw Moved("the type FastEndpoints.CommandHandlerRegistry");

    /// <summary>
    /// Points <c>QueueJobAsync</c> and <c>TriggerJobExecution</c> at <paramref name="host"/>'s own job
    /// queues, for every command type that host handles.
    /// </summary>
    /// <remarks>
    /// FastEndpoints keeps one queue per command type in a static, and every host that starts job
    /// workers overwrites it with its own. A host disposed since leaves a queue whose worker has
    /// stopped, and a <c>BarakoTestHost</c> leaves one polling its own database, so a job the
    /// fixture queues is stored but its wake reaches no worker on the fixture's database. The job
    /// then waits for the next storage probe, a minute apart. A test that needs the worker to run
    /// its jobs on time takes the queues back first.
    /// </remarks>
    public static void ClaimJobQueues(IServiceProvider host)
    {
        var handled = (System.Collections.IDictionary)host.GetRequiredService(CommandHandlerRegistry);
        lock (Gate)
        {
            foreach (var command in JobQueues.Keys.Cast<object>().ToList())
            {
                if (handled.Contains(command) && JobQueues[command] is { } queue)
                {
                    JobQueues[command] = host.GetRequiredService(queue.GetType());
                }
            }
        }
    }

    /// <summary>Takes the resolver the process has now, and puts it back when the result is disposed.</summary>
    public static IDisposable Keep()
    {
        lock (Gate)
        {
            return new Restore(Current());
        }
    }

    /// <summary>The resolver the process has now, or null when none has been set.</summary>
    public static IServiceResolver? Current() =>
        (bool)InstanceNotSet.GetValue(null)! ? null : (IServiceResolver?)Instance.GetValue(null);

    /// <summary>
    /// True when the resolver the process has now still answers, which it does not once the host it
    /// points at is disposed.
    /// </summary>
    public static bool IsUsable() => Answers(Current());

    private static bool Answers(IServiceResolver? resolver)
    {
        if (resolver is null)
        {
            return false;
        }

        try
        {
            resolver.TryResolve<IServiceResolver>();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Runs <paramref name="action"/> while no other thread can read or write the static.</summary>
    public static void Exclusively(Action action)
    {
        lock (Gate)
        {
            action();
        }
    }

    /// <summary>
    /// When the resolver points at a disposed host, moves it to the first of <paramref name="hosts"/>
    /// that is still up. A resolver that still answers is left alone, so a host a test is using keeps it.
    /// </summary>
    public static void RepairIfDisposed(IEnumerable<IServiceProvider> hosts)
    {
        lock (Gate)
        {
            if (IsUsable())
            {
                return;
            }

            foreach (var host in hosts)
            {
                try
                {
                    Write(host.GetRequiredService<IServiceResolver>());
                    return;
                }
                catch (ObjectDisposedException)
                {
                }
            }

            Console.Error.WriteLine(
                "FastEndpointsResolver: the static resolver points at a disposed host and no live host was left to "
                + "move it to (#1171). The next lookup made outside a request throws ObjectDisposedException.");
        }
    }

    /// <summary>
    /// <see cref="RepairIfDisposed"/> over the hosts of every live fixture, newest first. With several
    /// fixtures alive, one disposing must hand the resolver to a host of another.
    /// </summary>
    public static void RepairFromFallbacks()
    {
        lock (Gate)
        {
            RepairIfDisposed(Fallbacks.AsEnumerable().Reverse().SelectMany(f => f()).ToList());
        }
    }

    /// <summary>Sees every value written to the static, under the lock. For tests that pin what is written.</summary>
    internal static Action<IServiceResolver?>? OnWrite { get; set; }

    private static void Write(IServiceResolver? resolver)
    {
        OnWrite?.Invoke(resolver);
        Instance.SetValue(null, resolver);
    }

    private static InvalidOperationException Moved(string what) =>
        new($"FastEndpoints no longer has {what}, so FastEndpointsResolver cannot restore the static resolver. "
            + "Find where this FastEndpoints version keeps it and update FastEndpointsResolver.");

    /// <summary>
    /// Hosts, newest first, that <see cref="Keep"/> may fall back to when the resolver it captured
    /// belongs to a host disposed since. Disposing the result takes them off the list.
    /// </summary>
    public static IDisposable RegisterFallbacks(Func<IEnumerable<IServiceProvider>> hosts)
    {
        lock (Gate)
        {
            Fallbacks.Add(hosts);
        }

        return new Unregister(hosts);
    }

    private sealed class Unregister(Func<IEnumerable<IServiceProvider>> hosts) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                Fallbacks.Remove(hosts);
            }
        }
    }

    private sealed class Restore(IServiceResolver? previous) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                if (previous is null || Answers(previous))
                {
                    Write(previous);
                }

                RepairIfDisposed(Fallbacks.AsEnumerable().Reverse().SelectMany(f => f()).ToList());
            }
        }
    }
}
