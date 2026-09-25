using System.Reflection;

namespace barakoCMS.Modules;

/// <summary>A module assembly with a type that cannot load, and the names it could not resolve.</summary>
internal sealed record UnloadableModuleAssembly(Assembly Assembly, IReadOnlyList<string> Missing)
{
    public override string ToString()
    {
        var name = Assembly.GetName();
        return $"{name.Name} {name.Version} (missing {string.Join(", ", Missing)})";
    }
}
