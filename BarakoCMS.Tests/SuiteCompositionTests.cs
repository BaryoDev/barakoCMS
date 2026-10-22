// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Modules;
using BarakoCMS.AI;
using BarakoCMS.Email.Smtp;
using BarakoCMS.Files;
using BarakoCMS.Files.S3;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// all along and the list never added, plus Pages and Forms, plus this project's own probe. A module project added to
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
            "Email.Smtp",
            "ExternalAuth",
            "FeatureFlags",
            "Files",
            "Files.S3",
            "Forms",
            "Import",
            "Notifications",
            "Pages",
            "Portability",
            "Pwa",
            DiscoverableProbeModule.ModuleName,
        ]);
    }

    /// <summary>
    /// The published image reads keys from AWS or Google secret stores only because the Suite host
    /// adds both configuration sources, and only usefully if it adds them before AddBarakoCMS reads
    /// JWT:Key (#785). The host is top-level code nothing here can call, so its source is read.
    /// </summary>
    [Fact]
    public void The_suite_adds_both_secret_store_sources_before_the_core_reads_its_configuration()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds the Suite's Program.cs");
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "BarakoCMS.Suite", "Program.cs"));

        var core = program.IndexOf("builder.Services.AddBarakoCMS(builder.Configuration)", StringComparison.Ordinal);
        var aws = program.IndexOf("builder.Configuration.AddBarakoAwsSecrets();", StringComparison.Ordinal);
        var gcp = program.IndexOf("builder.Configuration.AddBarakoGcpSecrets();", StringComparison.Ordinal);

        core.Should().BePositive("the Suite registers the core");
        aws.Should().BeInRange(0, core - 1, "the AWS source must be in place before the core reads JWT:Key");
        gcp.Should().BeInRange(0, core - 1, "the Google source must be in place before the core reads JWT:Key");
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
    /// Email.Smtp is the Suite's email provider. It changes nothing until a host is configured, so
    /// a Suite with no email settings keeps core's mock rather than a provider with nowhere to send.
    /// </summary>
    [Fact]
    public void Email_goes_through_the_mock_until_an_smtp_host_is_configured()
    {
        var email = Registrations<IEmailService>(Build());

        email.Should().ContainSingle("Smtp registers nothing without a host");
        email[0].ImplementationType.Should().Be<MockEmailService>();
    }

    [Fact]
    public void Smtp_takes_over_once_a_host_is_configured()
    {
        var email = Registrations<IEmailService>(Build(("Modules:Email.Smtp:Host", "smtp.example.com")));

        // Modules register before core's TryAdd, so the mock is never added once Smtp is in.
        email.Should().HaveCount(1);
        email[0].ImplementationType.Should().Be<SmtpEmailService>();
    }

    /// <summary>
    /// The core and the Suite do not depend on Resend, a paid outside service (#1216). Hosts that
    /// still use it add the module from the private feed. The English verb in "Resend with force set
    /// to true" is allowed; a provider name in a type, a config key, an environment variable, a
    /// reference, a comment or a document is not. The one exception is the startup warning for a host that still sets the old key.
    /// </summary>
    [Fact]
    public void The_core_and_the_suite_name_no_Resend_provider()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent;
        root.Should().NotBeNull();

        var providerName = new Regex(@"Resend(?! with )|RESEND|resend\.com|resend:", RegexOptions.None);
        string[] extensions = [".cs", ".csproj", ".json", ".md", ".yml", ".yaml", ".props", ".sh"];
        var sep = Path.DirectorySeparatorChar;

        var files = new[] { "barakoCMS", "BarakoCMS.Suite" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root!.FullName, d), "*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Where(f => !f.EndsWith("LegacyEmailKeyCheck.cs"))
            .ToList();
        files.Should().NotBeEmpty("an empty scan proves nothing");
        files.Should().Contain(f => f.EndsWith("EmailSettingsProvider.cs"), "the scan reaches the email settings provider");

        var hits = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, n: i + 1)))
            .Where(x => providerName.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root!.FullName, x.f)}:{x.n}: {x.line.Trim()}")
            .ToList();
        hits.Should().BeEmpty("the core and the Suite must not name Resend as a provider");
    }

    [Fact]
    public void No_core_type_member_or_referenced_assembly_is_named_for_Resend()
    {
        var core = typeof(barakoCMS.Data.DataSeeder).Assembly;
        var names = core.GetTypes().Select(t => t.FullName ?? t.Name)
            .Concat(core.GetTypes().SelectMany(t => t.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)))
            .Concat(core.GetReferencedAssemblies().Select(a => a.Name ?? ""))
            .ToList();

        names.Should().NotBeEmpty();
        names.Should().NotContain(n => n.Contains("Resend", StringComparison.OrdinalIgnoreCase));
    }
}
