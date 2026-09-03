using Marten;
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

    /// <summary>
    /// Gives this module's capabilities to the roles that already reached its endpoints.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the endpoints would be reachable only through the legacy role-name fallback, and
    /// turning that off, which is the point of issue #443, would take the module away from every
    /// Admin. Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, PortabilityCapabilities.SeededRoles, PortabilityCapabilities.All, ct);
}
