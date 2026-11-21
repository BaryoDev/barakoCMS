namespace barakoCMS.Modules;

/// <summary>
/// Marks an assembly as holding barakoCMS modules. Discovery scans a library that reaches only
/// <c>BarakoCMS.Abstractions</c>, with no reference to the core, because it carries this attribute.
/// </summary>
/// <remarks>
/// <code>[assembly: BarakoModuleAssembly]</code>
/// or, in the project file, <c>&lt;AssemblyAttribute Include="barakoCMS.Modules.BarakoModuleAssembly" /&gt;</c>.
///
/// A library that references the core is scanned whether it carries this or not. A library built on
/// the contract alone without it is still scanned in 5.x, with a warning at startup naming it, and
/// is not scanned from 6.0.0.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class BarakoModuleAssemblyAttribute : Attribute;
