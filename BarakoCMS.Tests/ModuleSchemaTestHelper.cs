using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Tests;

/// <summary>
/// Registers a module's schema the way production does, through <see cref="IModuleSchema"/>.
/// </summary>
/// <remarks>
/// Tests used to call <c>module.ConfigureMarten(opts)</c> directly, which bypasses the ownership
/// check. Going through the same guarded surface means a module that starts reaching into core's
/// schema fails in the tests too, rather than only in production.
/// </remarks>
internal static class ModuleSchemaTestHelper
{
    public static void ConfigureVia(IBarakoModule module, StoreOptions options) =>
        module.ConfigureSchema(new ModuleSchema(options, module));
}
