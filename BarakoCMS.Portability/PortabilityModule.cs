using barakoCMS.Modules;

namespace BarakoCMS.Portability;

/// <summary>
/// Content import/export for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PortabilityModule()));</code>
///
/// <c>GET /api/portability/export</c> downloads a JSON bundle of content-type definitions plus
/// their content data (optionally filtered to specific types). <c>POST /api/portability/import</c>
/// takes a bundle and upserts the types (by name) then recreates the content via events, with a
/// dry-run mode. Operates within the current club (tenant-scoped content).
/// </summary>
public sealed class PortabilityModule : IBarakoModule
{
    public string Name => "Portability";
}
