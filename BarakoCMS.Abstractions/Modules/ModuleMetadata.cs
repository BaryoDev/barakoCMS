using System.Reflection;

namespace barakoCMS.Modules;

/// <summary>Helpers for declaring <see cref="IBarakoModule"/> identity members.</summary>
public static class ModuleMetadata
{
    /// <summary>
    /// The package version of the assembly <paramref name="moduleType"/> lives in, without build
    /// metadata, or null when the assembly states none.
    /// </summary>
    public static string? VersionOf(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        var info = moduleType.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
