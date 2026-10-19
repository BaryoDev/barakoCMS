using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Tests;

/// <summary>
/// The one module type in this assembly that discovery is meant to find.
/// </summary>
/// <remarks>
/// Public and top-level on purpose, because that is the shape a module shipped for other people to
/// reference has, and it is the shape discovery looks for. Every other <see cref="IBarakoModule"/>
/// in this project is a private nested test double, and <c>ModuleDiscoveryTests</c> holds that
/// discovery leaves those alone.
///
/// It does nothing unless a test asks, so a host that discovers it by accident (any test that calls
/// <c>AddBarakoCMS</c> without turning discovery off) runs one more no-op module.
/// <see cref="ReachForCoreTable"/> makes it index a core table for the current async flow only, so
/// the schema preflight has a discovered module with something to refuse.
/// </remarks>
public sealed class DiscoverableProbeModule : IBarakoModule
{
    public const string ModuleName = "Discoverable Probe";

    public string Name => ModuleName;

    private static readonly AsyncLocal<bool> Reaching = new();

    /// <summary>Until disposed, hosts built in this async flow get a module that indexes a core table.</summary>
    public static IDisposable ReachForCoreTable()
    {
        Reaching.Value = true;
        return new Reset();
    }

#pragma warning disable CS0618 // the deprecated hook is the only one that can reach a core table
    public void ConfigureMarten(StoreOptions options)
    {
        if (Reaching.Value)
            options.Schema.For<Content>().Index(x => x.LastModifiedBy);
    }
#pragma warning restore CS0618

    private sealed class Reset : IDisposable
    {
        public void Dispose() => Reaching.Value = false;
    }
}
