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

    public void ConfigureMarten(StoreOptions options)
    {
        // Delivery problems Resend reports. Global (no tenant): the recipient email is the key.
        options.Schema.For<EmailEvent>()
            .SingleTenanted()
            .DocumentAlias("email_events")
            .Index(x => x.Email);
    }
}
