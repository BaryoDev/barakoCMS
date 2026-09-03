using System.Reflection;

namespace BarakoCMS.Tests;

/// <summary>
/// The types an assembly can actually give you.
/// </summary>
/// <remarks>
/// <c>Assembly.GetTypes()</c> throws <see cref="ReflectionTypeLoadException"/> when any single type
/// in the assembly fails to load, and discards the ones that loaded fine along with it. The
/// exception's <c>Types</c> array keeps them, with a null in each failed slot.
///
/// That matters here because the capability gates scan every loaded assembly looking for module and
/// capability types. One unrelated type failing to load, an optional dependency absent on the
/// machine being the usual cause, would otherwise turn a structural gate into a crash that says
/// nothing about the thing it checks.
///
/// One helper rather than two, because the two scans had drifted: one caught this and the other did
/// not, which is the kind of difference nobody notices until the day it matters.
/// </remarks>
internal static class LoadableTypes
{
    public static IEnumerable<Type> In(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}
