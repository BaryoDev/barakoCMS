using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// Enables Resend as barakoCMS's email provider. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ResendEmailModule()));</code>
/// It registers <see cref="ResendEmailService"/> as <see cref="IEmailService"/>; because core now
/// registers its mock with TryAdd, this substitution wins. It also exposes a delivery webhook
/// (POST /api/webhooks/resend) that records bounces/complaints as <see cref="EmailEvent"/> documents.
/// </summary>
public sealed class ResendEmailModule : IBarakoModule
{
    public string Name => "Email.Resend";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<IEmailService, ResendEmailService>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Delivery problems Resend reports. Global (no tenant): the recipient email is the key.
        schema.For<EmailEvent>()
            .SingleTenanted()
            .DocumentAlias("email_events")
            .Index(x => x.Email);
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
        ModuleCapabilities.GrantAsync(session, ResendEmailCapabilities.SeededRoles, ResendEmailCapabilities.All, ct);
}
