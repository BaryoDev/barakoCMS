using System.Runtime.CompilerServices;

namespace BarakoCMS.Tests;

/// <summary>
/// Turns module discovery off for every host this process boots through <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> calls <c>AddBarakoCMS(builder.Configuration)</c> with no module list, so
/// discovery would find every module this project references, plus <see cref="DiscoverableProbeModule"/>,
/// and register them all. The fixtures wire module services and schema by hand, so discovery on top
/// of that registers each schema twice and the first boot fails on a duplicate index.
///
/// An environment variable rather than a key in <c>ConfigureAppConfiguration</c>, because with
/// minimal hosting those sources are added after <c>Program.cs</c> has already read the
/// configuration inside <c>AddBarakoCMS</c>. <c>IntegrationTestFixture</c> sets <c>JWT__Key</c> and
/// <c>DATABASE_URL</c> the same way for the same reason.
///
/// Process-wide and set before any test type is touched, so it does not depend on which fixture
/// happens to be constructed first. Tests that build their own configuration in memory are not
/// affected; <c>ModuleDiscoveryTests</c> and <c>SuiteCompositionTests</c> exercise discovery that way.
/// </remarks>
internal static class DiscoveryDefault
{
    [ModuleInitializer]
    internal static void Off() =>
        Environment.SetEnvironmentVariable("BarakoCMS__Modules__Discover", "false");
}

/// <summary>
/// The test process runs as Development, whichever test or fixture is scheduled first.
/// </summary>
/// <remarks>
/// <c>AddBarakoCMS</c> refuses an open <c>AllowedHosts</c> and a weak admin password outside
/// Development, reading <c>ASPNETCORE_ENVIRONMENT</c> from the process. About two dozen test
/// classes call it without <c>IntegrationTestFixture</c>, which is the only thing that set the
/// variable, so a filtered run saw it unset. Once any fixture starts, the process is Development
/// anyway, so this only removes the order dependence. A value set by the caller is left alone, and
/// the tests that need Production start a child process or pass the flag to the guard directly.
/// </remarks>
internal static class DevelopmentEnvironmentDefault
{
    [ModuleInitializer]
    internal static void Set()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        }
    }
}
