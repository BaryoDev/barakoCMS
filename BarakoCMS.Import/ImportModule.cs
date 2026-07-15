using barakoCMS.Modules;

namespace BarakoCMS.Import;

/// <summary>
/// Optional bulk-import module for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ImportModule()));</code>
/// It contributes two endpoints — analyze an uploaded .xlsx/CSV into a preview grid, and bulk-create
/// content from mapped records — with no document types or seed data of its own. Parsing is delegated
/// to the zero-dependency Talaan library.
/// </summary>
public sealed class ImportModule : IBarakoModule
{
    public string Name => "Import";
    // Default hooks: no services, no Marten docs, no seed. Endpoints live in this assembly and are
    // discovered via IBarakoModule.EndpointAssemblies (defaulting to this assembly).
}
