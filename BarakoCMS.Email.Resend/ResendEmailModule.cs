using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// Enables Resend as barakoCMS's email provider. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ResendEmailModule()));</code>
/// It registers <see cref="ResendEmailService"/> as <see cref="IEmailService"/>; because core now
/// registers its mock with TryAdd, this substitution wins.
/// </summary>
public sealed class ResendEmailModule : IBarakoModule
{
    public string Name => "Email.Resend";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<IEmailService, ResendEmailService>();
    }
}
