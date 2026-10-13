using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using BarakoCMS.Secrets.Aws;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
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

    // The adapter over the real client, against a stub of it: what each answer from Secrets Manager
    // becomes before the provider sees it.

    private static IAmazonSecretsManager Answering(GetSecretValueResponse response)
    {
        var client = Substitute.For<IAmazonSecretsManager>();
        client.GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>()).Returns(response);
        return client;
    }

    private static IAmazonSecretsManager Throwing(Exception error)
    {
        var client = Substitute.For<IAmazonSecretsManager>();
        client.GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(error));
        return client;
    }

    [Fact]
    public async Task The_adapter_returns_the_secret_string_it_asked_for_by_id()
    {
        var client = Answering(new GetSecretValueResponse { SecretString = "from-the-store" });

        var value = await AwsSecretsConfigurationExtensions.ReadAsync(client, "barakocms/jwt-key", CancellationToken.None);

        value.Should().Be("from-the-store");
        await client.Received(1).GetSecretValueAsync(
            Arg.Is<GetSecretValueRequest>(r => r.SecretId == "barakocms/jwt-key"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_adapter_reads_a_missing_secret_as_absent()
    {
        var value = await AwsSecretsConfigurationExtensions.ReadAsync(
            Throwing(new ResourceNotFoundException("Secrets Manager can't find the specified secret.")),
            "barakocms/missing", CancellationToken.None);

        value.Should().BeNull("the provider turns absent into 'does not exist', naming the secret");
    }

    [Fact]
    public async Task The_adapter_reduces_any_other_refusal_to_the_error_code()
    {
        var refusal = new AmazonSecretsManagerException("User is not authorized to perform GetSecretValue on arn:aws:secretsmanager:...")
        {
            ErrorCode = "AccessDeniedException",
        };

        var act = () => AwsSecretsConfigurationExtensions.ReadAsync(Throwing(refusal), "barakocms/jwt-key", CancellationToken.None);

        (await act.Should().ThrowAsync<SecretUnreadableException>()).Which.Message.Should().Be("AWS answered AccessDeniedException");
    }

    [Fact]
    public async Task The_adapter_refuses_a_binary_secret_without_printing_it()
    {
        var client = Answering(new GetSecretValueResponse { SecretBinary = new MemoryStream("binary-value-never-printed"u8.ToArray()) });

        var act = () => AwsSecretsConfigurationExtensions.ReadAsync(client, "barakocms/binary", CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<SecretUnreadableException>()).Which;
        thrown.Message.Should().Contain("binary").And.NotContain("binary-value-never-printed");
    }
}

