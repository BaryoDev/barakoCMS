using barakoCMS.Modules;

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
/// It does nothing, so a host that discovers it by accident (any test that calls
/// <c>AddBarakoCMS</c> without turning discovery off) runs one more no-op module.
/// </remarks>
public sealed class DiscoverableProbeModule : IBarakoModule
{
    public const string ModuleName = "Discoverable Probe";

    public string Name => ModuleName;
}
