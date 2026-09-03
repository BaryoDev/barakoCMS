using barakoCMS.Core.Interfaces;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Email.Smtp;

/// <summary>
/// Enables any SMTP relay as barakoCMS's email provider. Register it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new SmtpEmailModule()));</code>
/// It registers <see cref="SmtpEmailService"/> as <see cref="IEmailService"/>, and because core
/// registers its mock with TryAdd, this substitution wins.
/// </summary>
/// <remarks>
/// With no host configured it registers nothing. A deployment that upgrades, adds the package and
/// configures nothing keeps exactly the provider it had, which is the mock or whatever other module
/// it already registered. A module that registered itself unconfigured would take email over and
/// then fail every send, and the failure would look like the relay rather than like the upgrade.
/// </remarks>
public sealed class SmtpEmailModule : IBarakoModule
{
    public string Name => "Email.Smtp";

    public int ContractVersion => ModuleContract.Version;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = new SmtpOptions();
        configuration.Bind(options);

        if (string.IsNullOrWhiteSpace(options.Host))
            return;

        services.Configure<SmtpOptions>(configuration);
        services.AddScoped<IEmailService, SmtpEmailService>();
    }
}
