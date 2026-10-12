using BarakoCMS.Secrets.Aws;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// BarakoCMS.Secrets.Aws as a configuration source, against a stand-in for Secrets Manager (#785).
/// </summary>
public class AwsSecretsConfigurationTests
{
    private const string StoreJwtKey = "jwt-key-from-the-store-that-is-at-least-32-chars";

    private sealed class FakeStore(Dictionary<string, string?> secrets)
    {
        public List<string> Reads { get; } = [];

        public Task<string?> Read(string secretId, CancellationToken ct)
        {
            Reads.Add(secretId);
            return Task.FromResult(secrets.GetValueOrDefault(secretId));
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
        var configuration = Configuration(("JWT:Key", "from-the-environment"));
        var store = new FakeStore(new() { ["barakocms-jwt-key"] = StoreJwtKey });
        var sources = configuration.Sources.Count;

        configuration.AddBarakoAwsSecrets(store.Read);

        configuration.Sources.Should().HaveCount(sources);
        store.Reads.Should().BeEmpty();
        configuration["JWT:Key"].Should().Be("from-the-environment");
    }

    [Fact]
    public void A_mapped_secret_fills_its_key_and_wins_over_the_environment()
    {
        var configuration = Configuration(
            ("JWT:Key", "from-the-environment"),
            ("Secrets:Aws:Map:JWT:Key", "barakocms-jwt-key"),
            ("Secrets:Aws:Map:Modules:Files.S3:SecretKey", "barakocms-s3"));
        var store = new FakeStore(new() { ["barakocms-jwt-key"] = StoreJwtKey, ["barakocms-s3"] = "s3-secret" });

        configuration.AddBarakoAwsSecrets(store.Read);

        store.Reads.Should().BeEquivalentTo(["barakocms-jwt-key", "barakocms-s3"]);
        configuration["JWT:Key"].Should().Be(StoreJwtKey);
        configuration["Modules:Files.S3:SecretKey"].Should().Be("s3-secret");
    }

    [Fact]
    public void A_missing_secret_stops_startup_naming_the_secret_and_not_a_value_already_read()
    {
        var configuration = Configuration(
            ("Secrets:Aws:Map:A:Present", "barakocms-present"),
            ("Secrets:Aws:Map:JWT:Key", "barakocms-missing"));
        var store = new FakeStore(new() { ["barakocms-present"] = "present-value-never-to-be-printed" });

        var act = () => configuration.AddBarakoAwsSecrets(store.Read);

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        store.Reads.Should().Contain("barakocms-present", "the message must be checked after a value was read");
        thrown.Message.Should().Contain("'barakocms-missing'").And.Contain("JWT:Key").And.Contain("does not exist");
        thrown.Message.Should().NotContain("present-value-never-to-be-printed");
    }

    [Fact]
    public void An_empty_secret_stops_startup()
    {
        var configuration = Configuration(("Secrets:Aws:Map:JWT:Key", "barakocms-jwt-key"));
        var store = new FakeStore(new() { ["barakocms-jwt-key"] = "   " });

        var act = () => configuration.AddBarakoAwsSecrets(store.Read);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'barakocms-jwt-key'*JWT:Key*is empty*");
    }

    [Fact]
    public void A_store_that_refuses_stops_startup_naming_the_secret_and_keeping_the_cause()
    {
        var configuration = Configuration(("Secrets:Aws:Map:JWT:Key", "barakocms-jwt-key"));
        var refusal = new InvalidOperationException("AccessDeniedException: not authorized to perform GetSecretValue");

        var act = () => configuration.AddBarakoAwsSecrets((_, _) => Task.FromException<string?>(refusal));

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("'barakocms-jwt-key'").And.Contain("JWT:Key").And.Contain("AccessDeniedException");
        thrown.InnerException.Should().BeSameAs(refusal);
    }
}

