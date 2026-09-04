using barakoCMS.Core.Interfaces;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Services;
using BarakoCMS.Email.Smtp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// Whether the module takes email over, which is a decision made entirely from configuration.
/// </summary>
/// <remarks>
/// Asserted on the registration rather than on a resolved instance: resolving the SMTP service
/// pulls in the core settings provider and therefore a database, and the claim here is about which
/// implementation is registered, not about what it does once it is.
///
/// Everything goes through <c>AddBarakoCMS</c> rather than calling <c>ConfigureServices</c>
/// directly, because the part that can silently break is the wiring: the module is handed its own
/// <c>Modules:Email.Smtp</c> section by the host, and a test that hands it a section itself would
/// keep passing if that stopped happening.
/// </remarks>
public class SmtpEmailModuleTests
{
    private static ServiceDescriptor EmailRegistration(params (string Key, string Value)[] settings)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        foreach (var (key, value) in settings) pairs[key] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();

        var services = new ServiceCollection();
        // Discovery off: with it on, the Resend module this project references registers its own
        // IEmailService and the single-registration claim below is about SMTP alone.
        services.AddBarakoCMS(config, m => { m.Discover = false; m.Add(new SmtpEmailModule()); });

        var registrations = services.Where(d => d.ServiceType == typeof(IEmailService)).ToList();

        registrations.Should().ContainSingle(
            "two registrations would make which provider sends depend on resolution order");

        return registrations[0];
    }

    /// <summary>
    /// An upgrade that adds the package and configures nothing changes nothing.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is quiet: a module that registered itself unconfigured would take
    /// email over from whatever was there and then fail every send, and it would read as the relay
    /// being down rather than as the upgrade.
    /// </remarks>
    [Fact]
    public void With_no_host_configured_the_module_registers_no_provider()
    {
        EmailRegistration().ImplementationType.Should().Be<MockEmailService>();
    }

    /// <summary>The pair. A refusal test alone passes against a module that never registers.</summary>
    [Fact]
    public void With_a_host_configured_it_replaces_the_mock()
    {
        EmailRegistration(("Modules:Email.Smtp:Host", "smtp.example.com"))
            .ImplementationType.Should().Be<SmtpEmailService>();
    }

    [Fact]
    public void A_blank_host_counts_as_unconfigured()
    {
        // An environment variable set to nothing is how this arrives in practice, and a host of ""
        // would otherwise register a provider that cannot connect to anything.
        EmailRegistration(("Modules:Email.Smtp:Host", "   "))
            .ImplementationType.Should().Be<MockEmailService>();
    }

    /// <summary>
    /// It reads its own section and only its own. The module declares no legacy section, so a
    /// root-level <c>Smtp:Host</c> is somebody else's setting.
    /// </summary>
    [Fact]
    public void A_root_level_smtp_section_does_not_configure_it()
    {
        EmailRegistration(("Smtp:Host", "smtp.example.com"))
            .ImplementationType.Should().Be<MockEmailService>();
    }
}
