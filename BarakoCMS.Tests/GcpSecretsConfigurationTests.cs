using BarakoCMS.Secrets.Gcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// BarakoCMS.Secrets.Gcp as a configuration source, against a stand-in for Secret Manager (#785).
/// </summary>
public class GcpSecretsConfigurationTests
{
    private sealed class FakeStore(Dictionary<string, string?> versions)
    {
        public List<string> Reads { get; } = [];

        public Task<string?> Read(string name, CancellationToken ct)
        {
            Reads.Add(name);
            return Task.FromResult(versions.GetValueOrDefault(name));
        }
    }

    private static ConfigurationManager Configuration(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return configuration;
    }

    [Fact]
    public void Without_a_map_nothing_is_added_and_the_store_is_never_asked()
    {
        var configuration = Configuration(("JWT:Key", "from-the-environment"), ("Secrets:Gcp:Project", "clinic"));
        var store = new FakeStore([]);
        var sources = configuration.Sources.Count;

        configuration.AddBarakoGcpSecrets(store.Read);

        configuration.Sources.Should().HaveCount(sources);
        store.Reads.Should().BeEmpty();
        configuration["JWT:Key"].Should().Be("from-the-environment");
    }

    [Fact]
    public void A_short_name_reads_the_latest_version_in_the_project_and_wins_over_the_environment()
    {
        var configuration = Configuration(
            ("JWT:Key", "from-the-environment"),
            ("Secrets:Gcp:Project", "clinic"),
            ("Secrets:Gcp:Map:JWT:Key", "barakocms-jwt-key"));
        var store = new FakeStore(new() { ["projects/clinic/secrets/barakocms-jwt-key/versions/latest"] = "from-the-store" });

        configuration.AddBarakoGcpSecrets(store.Read);

        store.Reads.Should().Equal("projects/clinic/secrets/barakocms-jwt-key/versions/latest");
        configuration["JWT:Key"].Should().Be("from-the-store");
    }

    [Fact]
    public void A_full_name_is_used_as_it_is_and_a_pinned_version_is_kept()
    {
        var configuration = Configuration(
            ("Secrets:Gcp:Project", "ignored-for-full-names"),
            ("Secrets:Gcp:Map:Secrets:Key", "projects/other/secrets/encryption"),
            ("Secrets:Gcp:Map:Mfa:Key", "projects/other/secrets/mfa/versions/3"));
        var store = new FakeStore(new()
        {
            ["projects/other/secrets/encryption/versions/latest"] = "encryption-key",
            ["projects/other/secrets/mfa/versions/3"] = "mfa-key",
        });

        configuration.AddBarakoGcpSecrets(store.Read);

        store.Reads.Should().BeEquivalentTo(
            ["projects/other/secrets/encryption/versions/latest", "projects/other/secrets/mfa/versions/3"]);
        configuration["Secrets:Key"].Should().Be("encryption-key");
        configuration["Mfa:Key"].Should().Be("mfa-key");
    }

    [Fact]
    public void A_short_name_with_no_project_stops_startup_before_asking_the_store()
    {
        var configuration = Configuration(("Secrets:Gcp:Map:JWT:Key", "barakocms-jwt-key"));
        var store = new FakeStore([]);

        var act = () => configuration.AddBarakoGcpSecrets(store.Read);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'barakocms-jwt-key'*JWT:Key*Secrets:Gcp:Project is not set*");
        store.Reads.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_secret_stops_startup_naming_the_secret()
    {
        var configuration = Configuration(
            ("Secrets:Gcp:Project", "clinic"),
            ("Secrets:Gcp:Map:JWT:Key", "barakocms-missing"));

        var act = () => configuration.AddBarakoGcpSecrets(new FakeStore([]).Read);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'projects/clinic/secrets/barakocms-missing/versions/latest'*JWT:Key*does not exist*");
    }
}
