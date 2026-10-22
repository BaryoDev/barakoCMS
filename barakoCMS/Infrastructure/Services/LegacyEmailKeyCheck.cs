using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Warns once at startup when a host still sets the key of the email module that left this
/// repository (#1216) and mail would go to the mock. Without it that host upgrades and stops
/// sending, with nothing in the log.
/// </summary>
internal static class LegacyEmailKeyCheck
{
    public const string Warning =
        "A Resend API key is set (Resend:ApiKey or RESEND_API_KEY) but no email provider is registered, "
        + "so email goes to the mock service and nothing is sent. The Resend module left this repository "
        + "and the core no longer reads those names. Configure SMTP, or install the private module and set "
        + "Email:ApiKey. See docs/configuring-email.md.";

    public static void WarnIfIgnored(IServiceCollection services, IConfiguration configuration)
    {
        var keySet = !string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"])
                     || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESEND_API_KEY"));
        if (!keySet)
            return;

        var email = services.LastOrDefault(d => d.ServiceType == typeof(barakoCMS.Core.Interfaces.IEmailService));
        if (email?.ImplementationType == typeof(MockEmailService))
            Log.Warning(Warning);
    }
}
