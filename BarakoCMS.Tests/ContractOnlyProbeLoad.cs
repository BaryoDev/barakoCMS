using System.Runtime.CompilerServices;

namespace BarakoCMS.Tests;

/// <summary>
/// Loads the contract-only probe (#972) before any host is built, so every host in the process
/// maps its route.
/// </summary>
/// <remarks>
/// FastEndpoints scans only the assemblies already loaded, and no host references the probe. It
/// used to load when the first test touched one of its types, so its route was there or not by
/// test order: <see cref="ContractOnlyModuleEndpointTests"/> run alone answered 404, and the
/// private site route review saw the route on some CI shards only.
/// </remarks>
internal static class ContractOnlyProbeLoad
{
    [ModuleInitializer]
    internal static void Load() => _ = typeof(ContractOnlyProbe.ContractOnlyPingEndpoint).Assembly;
}
