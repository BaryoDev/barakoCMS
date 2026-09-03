using barakoCMS.Modules;
using Marten;

namespace BarakoCMS.Pwa;

/// <summary>
/// Records PWA installs / installed-app launches. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new PwaModule()));</code>
/// It exposes <c>POST /api/pwa/report</c> (called by the client, works anonymously or tied to the
/// signed-in user) and <c>GET /api/pwa/installs</c> (admin). Persists <see cref="PwaInstall"/> globally
/// — no per-tenant partition, the tenant is kept as data.
/// </summary>
public sealed class PwaModule : IBarakoModule
{
    public string Name => "Pwa";

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<PwaInstall>()
            .SingleTenanted()
            .DocumentAlias("pwa_installs")
            .Index(x => x.DeviceId)
            .Index(x => x.UserId);
    }

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
        ModuleCapabilities.GrantAsync(session, PwaCapabilities.SeededRoles, PwaCapabilities.All, ct);
}
