using System.Reflection;
using FastEndpoints;

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

    /// <summary>Takes the resolver the process has now, and puts it back when the result is disposed.</summary>
    public static IDisposable Keep()
    {
        var previous = (bool)InstanceNotSet.GetValue(null)! ? null : Instance.GetValue(null);
        return new Restore(previous);
    }

    private static InvalidOperationException Moved(string what) =>
        new($"FastEndpoints no longer has {what}, so FastEndpointsResolver cannot restore the static resolver. "
            + "Find where this FastEndpoints version keeps it and update FastEndpointsResolver.");

    private sealed class Restore(object? previous) : IDisposable
    {
        public void Dispose() => Instance.SetValue(null, previous);
    }
}
