using System.Reflection;
using barakoCMS.Models;
using barakoCMS.Modules;
using FastEndpoints;
using Microsoft.AspNetCore.Routing;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>One name a role can be granted, and where it comes from.</summary>
/// <param name="Name">The capability, as an endpoint asks for it.</param>
/// <param name="Source">
/// <c>core</c> for a name core declares, otherwise the registered module's <see cref="IBarakoModule.Name"/>.
/// A module whose endpoints are served but which was never registered as an <see cref="IBarakoModule"/>
/// is named by its assembly instead, so the entry still says where the name came from.
/// </param>
/// <param name="Note">Set only where a name needs explaining, which today is the wildcard.</param>
internal sealed record KnownCapability(string Name, string Source, string? Note = null);

/// <summary>
/// Every capability this instance understands: core's <see cref="SystemCapabilities.Known"/> plus
/// the name every registered endpoint asks for through <see cref="CapabilityGate.RequireCapability"/>.
/// </summary>
/// <remarks>
/// Read off the routing table rather than off a list a module has to maintain. A module already
/// declares its capabilities in the one place that cannot drift, its <c>Configure()</c>, and
/// <see cref="CapabilityGateProcessor"/> reads exactly that metadata to enforce them. Reading the
/// same metadata here means the vocabulary is what the gates enforce, and a module needs no new
/// contract member to be listed. A module you have not installed contributes nothing, which is
/// right: its names would grant access to nothing on this instance.
///
/// Computed once per host on first use, since the endpoint set is fixed after startup. It cannot be
/// computed in the constructor because the routing table is built after the container is.
///
/// See issue #490.
/// </remarks>
internal sealed class CapabilityVocabulary
{
    public const string CoreSource = "core";

    public const string WildcardNote =
        "Satisfies every capability, including ones added after the role was written. "
      + "A role holding it reaches every gated endpoint on this instance.";

    /// <summary>
    /// Whether a role write refuses a capability name this instance does not know. Default off:
    /// the name is saved, logged and returned as <c>unknownCapabilities</c>, so a module installed
    /// later that declares it starts working without a re-edit.
    /// </summary>
    public const string RefuseUnknownKey = "Roles:RefuseUnknownCapabilities";

    private readonly IServiceProvider _services;
    private readonly Lazy<IReadOnlyList<KnownCapability>> _entries;

    public CapabilityVocabulary(IServiceProvider services)
    {
        _services = services;
        _entries = new Lazy<IReadOnlyList<KnownCapability>>(Collect);
    }

    /// <summary>Ordered by name so two calls, and two deployments of the same set, agree.</summary>
    public IReadOnlyList<KnownCapability> Entries => _entries.Value;

    public bool IsKnown(string capability) =>
        !string.IsNullOrWhiteSpace(capability)
        && Entries.Any(e => string.Equals(e.Name, capability, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names in <paramref name="requested"/> this instance does not know, once each.</summary>
    public IReadOnlyList<string> Unknown(IEnumerable<string> requested) =>
        requested
            .Where(name => !IsKnown(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string UnknownMessage(string capability) =>
        $"Unknown capability '{capability}'. GET /api/capabilities lists the names this instance understands.";

    private IReadOnlyList<KnownCapability> Collect()
    {
        var core = typeof(CapabilityVocabulary).Assembly;
        var modules = _services.GetServices<IBarakoModule>().ToList();

        var byName = new Dictionary<string, KnownCapability>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in SystemCapabilities.Known)
        {
            byName[name] = new KnownCapability(name, CoreSource, name == SystemCapabilities.All ? WildcardNote : null);
        }

        var required = _services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => (
                Required: endpoint.Metadata.GetMetadata<RequiredCapability>(),
                Assembly: endpoint.Metadata.OfType<EndpointDefinition>().FirstOrDefault()?.EndpointType.Assembly))
            .Where(x => x.Required is not null && x.Assembly is not null);

        foreach (var (metadata, assembly) in required)
        {
            var name = metadata!.Capability;
            if (byName.ContainsKey(name)) continue;

            byName[name] = new KnownCapability(name, assembly == core ? CoreSource : SourceOf(assembly!, modules));
        }

        return byName.Values
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string SourceOf(Assembly assembly, IReadOnlyList<IBarakoModule> modules) =>
        modules.FirstOrDefault(m => m.EndpointAssemblies.Contains(assembly))?.Name
        ?? assembly.GetName().Name
        ?? "unknown";
}
