// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Modules;
using BarakoCMS.AI;
using BarakoCMS.Email.Smtp;
using BarakoCMS.Files;
using BarakoCMS.Files.S3;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What <c>BarakoCMS.Suite</c> gets from <c>AddBarakoCMS(configuration)</c> with no module list.
/// </summary>
/// <remarks>
/// The Suite used to add thirteen modules by hand, two of them with a comment saying they stay
/// dormant until configured. That list is gone, so the claims it carried are pinned here instead,
/// against this process's dependency context, which references the same module projects the Suite
/// does. Nothing here boots the Suite: the claim is about what <c>AddBarakoCMS</c> registers.
/// </remarks>
public class SuiteCompositionTests
{
    private static IServiceCollection Build(params (string Key, string Value)[] settings)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        foreach (var (key, value) in settings) pairs[key] = value;

        var services = new ServiceCollection();
        Host.AddBarakoCMS(services, new ConfigurationBuilder().AddInMemoryCollection(pairs).Build(), configureModules: null);
        return services;
    }

    private static IReadOnlyList<ServiceDescriptor> Registrations<TService>(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(TService)).ToList();

    /// <summary>
    /// The thirteen the hand-written list named, plus Email.Smtp, which the Suite project referenced
    /// all along and the list never added, plus this project's own probe. A module project added to
    /// the Suite without being added here fails this test, which is the point: the set is pinned.
    /// </summary>
    [Fact]
    public void Discovery_finds_every_first_party_module_the_process_references()
    {
        var names = Registrations<IBarakoModule>(Build())
            .Select(d => ((IBarakoModule)d.ImplementationInstance!).Name)
            .ToList();

        names.Should().BeEquivalentTo(
        [
            "Accounting",
            "AI",
            "Analytics.Umami",
            "DeviceTrust",
            "Diagnostics",
            "Email.Resend",
            "Email.Smtp",
            "ExternalAuth",
            "FeatureFlags",
            "Files",
            "Files.S3",
            "Forms",
            "Import",
            "Portability",
            "Pwa",
            DiscoverableProbeModule.ModuleName,
        ]);
    }

    [Fact]
    public void S3_stays_dormant_until_a_bucket_is_configured()
    {
        var storage = Registrations<IFileStorage>(Build());

        storage.Should().ContainSingle();
        storage[0].ImplementationType.Should().Be<PostgresFileStorage>("with no bucket, Postgres keeps serving");
    }

    /// <summary>The pair. A dormancy test alone passes against a module that never wakes.</summary>
    [Fact]
    public void S3_takes_over_once_a_bucket_is_configured()
    {
        var storage = Registrations<IFileStorage>(Build(("Modules:Files.S3:Bucket", "media")));

        storage.Should().ContainSingle("S3 removes the Postgres registration rather than sitting beside it");
        storage[0].ImplementationType.Should().Be<S3FileStorage>();
    }

    [Fact]
    public async Task AI_stays_off_until_enabled()
    {
        await using var provider = Build().BuildServiceProvider();

        provider.GetRequiredService<IOptions<AiOptions>>().Value.IsConfigured
            .Should().BeFalse("the semantic endpoints answer empty until Modules:AI:Enabled is true");
    }

    [Fact]
    public async Task AI_switches_on_from_its_own_section()
    {
        await using var provider = Build(
            ("Modules:AI:Enabled", "true"),
            ("Modules:AI:EmbeddingBaseUrl", "http://ollama:11434")).BuildServiceProvider();

        provider.GetRequiredService<IOptions<AiOptions>>().Value.IsConfigured.Should().BeTrue();
    }

    /// <summary>
    /// Email.Smtp joined the set by being referenced, so this pins that it changes nothing until
    /// asked: Resend is the provider, exactly as the hand-written list left it.
    /// </summary>
    [Fact]
    public void Email_goes_through_Resend_until_an_smtp_host_is_configured()
    {
        var email = Registrations<IEmailService>(Build());

        email.Should().ContainSingle("Smtp registers nothing without a host, and core's mock yields to Resend");
        email[0].ImplementationType.Should().NotBe<MockEmailService>();
        email[0].ImplementationType.Should().NotBe<SmtpEmailService>();
    }

    [Fact]
    public void Smtp_takes_over_once_a_host_is_configured()
    {
        var email = Registrations<IEmailService>(Build(("Modules:Email.Smtp:Host", "smtp.example.com")));

        // Smtp configures after Resend (sorted by type name), so its registration is the last one
        // and the one the container resolves.
        email.Should().HaveCount(2);
        email[^1].ImplementationType.Should().Be<SmtpEmailService>();
    }
}
