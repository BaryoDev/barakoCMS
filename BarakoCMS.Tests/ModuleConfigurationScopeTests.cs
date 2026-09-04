// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// A module must receive its own configuration section and nothing else. The root holds the
/// database connection string, the JWT signing key and the seeded admin credentials, and handing
/// those to every referenced package was authority granted by accident.
///
/// These assert on what a module can READ, not on how the host is written, so they keep holding if
/// the plumbing is rewritten.
/// </summary>
public class ModuleConfigurationScopeTests
{
    private sealed class Probe : IBarakoModule
    {
        private readonly string? _legacy;
        public Probe(string name, string? legacy = null) { Name = name; _legacy = legacy; }
        public string Name { get; }
        public string? LegacyConfigurationSection => _legacy;
        public IConfiguration? Seen { get; private set; }
        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => Seen = configuration;
        public void ConfigureMarten(StoreOptions options) { }
    }

    private static IConfiguration Root(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void A_module_cannot_read_the_connection_string()
    {
        var root = Root(
            ("ConnectionStrings:Postgres", "Host=db;Password=hunter2"),
            ("JWT:SigningKey", "super-secret"),
            ("InitialAdmin:Password", "admin-password"),
            ("Modules:Probe:ApiKey", "the-module-key"));

        var seen = Host.ModuleConfiguration(root, new Probe("Probe"));

        seen["ConnectionStrings:Postgres"].Should().BeNull("a module has no business reading the database password");
        seen["JWT:SigningKey"].Should().BeNull("a module that can read the signing key can mint tokens for anyone");
        seen["InitialAdmin:Password"].Should().BeNull();
        seen["ApiKey"].Should().Be("the-module-key", "its own settings must still arrive");
    }

    [Fact]
    public void Its_own_section_arrives_rooted_so_keys_are_read_directly()
    {
        var root = Root(("Modules:Probe:Nested:Value", "42"));
        var seen = Host.ModuleConfiguration(root, new Probe("Probe"));
        seen["Nested:Value"].Should().Be("42");
    }

    [Fact]
    public void A_module_with_no_configuration_gets_an_empty_section_not_the_root()
    {
        var root = Root(("ConnectionStrings:Postgres", "Host=db;Password=hunter2"));
        var seen = Host.ModuleConfiguration(root, new Probe("Probe"));
        seen["ConnectionStrings:Postgres"].Should().BeNull();
    }

    [Fact]
    public void A_dot_in_a_module_name_still_resolves_to_its_own_section()
    {
        // Real names include Analytics.Umami and Files.S3. A dot is part of the key, not a separator.
        var root = Root(("Modules:Analytics.Umami:ApiKey", "k"));
        var seen = Host.ModuleConfiguration(root, new Probe("Analytics.Umami"));
        seen["ApiKey"].Should().Be("k");
    }

    [Fact]
    public void The_legacy_section_is_used_only_when_the_scoped_one_is_absent()
    {
        // Upgrading must not silently un-configure a module that reads a root section today.
        var legacyOnly = Root(("Umami:ApiKey", "old"));
        Host.ModuleConfiguration(legacyOnly, new Probe("Analytics.Umami", "Umami"))["ApiKey"]
            .Should().Be("old", "an existing deployment keeps working");

        // And once moved, the scoped section wins rather than the two fighting.
        var both = Root(("Umami:ApiKey", "old"), ("Modules:Analytics.Umami:ApiKey", "new"));
        Host.ModuleConfiguration(both, new Probe("Analytics.Umami", "Umami"))["ApiKey"]
            .Should().Be("new");
    }

    [Fact]
    public void The_legacy_fallback_never_widens_to_the_root()
    {
        // A legacy section is still a section. Falling back must not hand over everything.
        var root = Root(("Umami:ApiKey", "old"), ("ConnectionStrings:Postgres", "Host=db;Password=hunter2"));
        var seen = Host.ModuleConfiguration(root, new Probe("Analytics.Umami", "Umami"));
        seen["ApiKey"].Should().Be("old");
        seen["ConnectionStrings:Postgres"].Should().BeNull();
    }

    /// <summary>
    /// The one that matters. Everything above tests the helper; this tests the WIRING, by going
    /// through AddBarakoCMS and asking the module what it was actually handed.
    ///
    /// Written after the other six passed with the vulnerability deliberately reintroduced: putting
    /// the root back at the call site left them all green, because none of them went through it.
    /// A test that cannot fail against the bug it names is not a gate.
    /// </summary>
    [Fact]
    public void AddBarakoCMS_hands_each_module_only_its_own_section()
    {
        var probe = new Probe("Probe");
        var root = Root(
            // AddBarakoCMS refuses to build without a database outside Development. This used to
            // pass on ambient state: IntegrationTestFixture sets ASPNETCORE_ENVIRONMENT on the
            // process, and whether its constructor had run first decided whether this test saw a
            // Development host. Say what the test needs instead of inheriting it.
            ("ConnectionStrings:DefaultConnection", "Host=db;Username=u;Password=hunter2;Database=d"),
            ("ConnectionStrings:Postgres", "Host=db;Username=u;Password=hunter2;Database=d"),
            ("JWT:Key", "a-signing-key-that-is-comfortably-over-32-characters-long"),
            ("JWT:Issuer", "test"),
            ("JWT:Audience", "test"),
            ("InitialAdmin:Password", "admin-password"),
            ("Modules:Probe:ApiKey", "the-module-key"));

        // Discovery off: the probe is the subject, not every module this project references.
        Host.AddBarakoCMS(new ServiceCollection(), root, m => { m.Discover = false; m.Add(probe); });

        probe.Seen.Should().NotBeNull("the module must have been configured at all");
        probe.Seen!["ApiKey"].Should().Be("the-module-key", "its own settings must arrive");
        probe.Seen["ConnectionStrings:Postgres"].Should()
            .BeNull("a module must never receive the database password");
        probe.Seen["ConnectionStrings:DefaultConnection"].Should()
            .BeNull("least of all the one the host actually connects with");
        probe.Seen["JWT:Key"].Should()
            .BeNull("a module that can read the signing key can mint a token for any user");
        probe.Seen["InitialAdmin:Password"].Should().BeNull();
    }

    [Fact]
    public void A_half_finished_migration_keeps_keys_from_both_sections()
    {
        // The case that made picking one whole section wrong: an operator moves Enabled across and
        // leaves BaseUrl behind. Choosing the scoped section outright silently discarded BaseUrl and
        // the module ran misconfigured with nothing said.
        var root = Root(
            ("Modules:Analytics.Umami:Enabled", "true"),   // moved
            ("Umami:BaseUrl", "https://umami.example"),    // not moved yet
            ("ConnectionStrings:Postgres", "Host=db;Password=hunter2"));

        var seen = Host.ModuleConfiguration(root, new Probe("Analytics.Umami", "Umami"));

        seen["Enabled"].Should().Be("true");
        seen["BaseUrl"].Should().Be("https://umami.example", "a key left in the old section must still be read");
        seen["ConnectionStrings:Postgres"].Should().BeNull("merging must not widen to the root");
    }

    [Fact]
    public void Where_both_sections_define_a_key_the_scoped_one_wins()
    {
        var root = Root(
            ("Umami:ApiKey", "old"),
            ("Modules:Analytics.Umami:ApiKey", "new"),
            ("Umami:BaseUrl", "https://old.example"));

        var seen = Host.ModuleConfiguration(root, new Probe("Analytics.Umami", "Umami"));

        seen["ApiKey"].Should().Be("new", "the migrated value is the intended one");
        seen["BaseUrl"].Should().Be("https://old.example");
    }
}
