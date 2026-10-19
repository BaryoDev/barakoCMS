using barakoCMS.Features.Modules.Configuration;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.Modules;

/// <summary>
/// What a module's configuration view lets out. Every secret test puts the secret beside a value
/// that must survive, so a reader that removed everything fails as surely as one that removed nothing.
/// </summary>
public class ModuleConfigurationReaderTests
{
    private const string Module = "Email.Smtp";
    private const string Section = "Modules:Email.Smtp";

    // Fixture values, not real credentials.
    private const string Pass = "Zx9-fixture-pass";
    private const string ApiKeyValue = "fixture-api-value-77";
    private const string ConnValue = "fixture-conn-value-88";

    private static ShowableConfigurationKey K(string key, ConfigurationScope scope = ConfigurationScope.Deployment) =>
        new(key, scope);

    private static ModuleCatalogueEntry Declaring(params ShowableConfigurationKey[] keys) =>
        new(Module, 1, Enabled: true) { ConfigurationView = new ModuleConfigurationView(keys) };

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static string Everything(ModuleConfigurationResponse response) =>
        System.Text.Json.JsonSerializer.Serialize(response);

    [Fact]
    public void The_listed_keys_come_back_with_their_values()
    {
        var reader = new ModuleConfigurationReader(Config(
            ($"{Section}:Host", "mail.example.com"), ($"{Section}:Port", "587")));

        var result = reader.Read(Declaring(K("Host"), K("Port")), includeDeployment: true);

        result.Entries.Should().HaveCount(2);
        result.Entries.Select(e => (e.Key, e.Value, e.Redacted)).Should().Equal(
            [("Host", "mail.example.com", false), ("Port", "587", false)]);
        result.Entries.Should().OnlyContain(e => e.Scope == "deployment");
    }

    [Fact]
    public void A_module_that_declares_nothing_returns_an_empty_list_not_every_key()
    {
        var reader = new ModuleConfigurationReader(Config(($"{Section}:Host", "mail.example.com")));

        var undeclared = reader.Read(new ModuleCatalogueEntry(Module, 1, true), includeDeployment: true);
        var empty = reader.Read(Declaring(), includeDeployment: true);

        undeclared.Entries.Should().BeEmpty();
        empty.Entries.Should().BeEmpty();
        Everything(undeclared).Should().NotContain("mail.example.com");
    }

    [Fact]
    public void A_password_an_api_key_and_a_connection_string_are_never_returned_even_when_listed()
    {
        var reader = new ModuleConfigurationReader(Config(
            ($"{Section}:Host", "mail.example.com"),
            ($"{Section}:Password", Pass),
            ($"{Section}:ApiKey", ApiKeyValue),
            ($"{Section}:ConnectionString", ConnValue)));

        var result = reader.Read(
            Declaring(K("Host"), K("Password"), K("ApiKey"), K("ConnectionString")), includeDeployment: true);

        result.Entries.Should().HaveCount(4);
        var body = Everything(result);
        body.Should().Contain("mail.example.com");
        body.Should().NotContain(Pass).And.NotContain(ApiKeyValue).And.NotContain(ConnValue);
        result.Entries.Where(e => e.Key != "Host").Should().OnlyContain(e => e.Redacted && e.Value == null);
    }

    [Fact]
    public void An_unlisted_key_comes_back_redacted()
    {
        var reader = new ModuleConfigurationReader(Config(
            ($"{Section}:Host", "mail.example.com"), ($"{Section}:Relay", "internal-relay-9")));

        var result = reader.Read(Declaring(K("Host")), includeDeployment: true);

        result.Entries.Should().HaveCount(2);
        var relay = result.Entries.Single(e => e.Key == "Relay");
        relay.Redacted.Should().BeTrue();
        relay.Value.Should().BeNull();
        Everything(result).Should().NotContain("internal-relay-9");
    }

    [Theory]
    [InlineData("smtp://user:hunter2x@mail.example.com", "hunter2x")]
    [InlineData("mail.example.com;password=hunter2x", "hunter2x")]
    [InlineData("Server=db;Pwd=hunter2x", "hunter2x")]
    [InlineData("Bearer abcdefghijklmnop123", "abcdefghijklmnop123")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("7f3a9c2e5b1d4f6a8c0e2b4d6f8a1c3e5b7d9f1a", "7f3a9c2e5b1d4f6a8c0e2b4d6f8a1c3e5b7d9f1a")]
    public void A_listed_key_whose_value_holds_a_credential_is_redacted(string value, string secret)
    {
        var reader = new ModuleConfigurationReader(Config(($"{Section}:Host", value), ($"{Section}:Port", "587")));

        var result = reader.Read(Declaring(K("Host"), K("Port")), includeDeployment: true);

        result.Entries.Should().HaveCount(2);
        result.Entries.Single(e => e.Key == "Host").Redacted.Should().BeTrue();
        result.Entries.Single(e => e.Key == "Port").Value.Should().Be("587");
        Everything(result).Should().NotContain(secret);
    }

    [Fact]
    public void Nested_objects_array_elements_and_numbers_follow_the_same_rules()
    {
        var reader = new ModuleConfigurationReader(Config(
            ($"{Section}:Relay:Host", "relay.example.com"),
            ($"{Section}:Relay:Auth:Secret", Pass),
            ($"{Section}:Hosts:0", "a.example.com"),
            ($"{Section}:Hosts:1", "smtp://u:hunter2x@b.example.com"),
            ($"{Section}:Retries", "3"),
            ($"{Section}:Other:Port", "25")));

        // Listing a parent covers what is beneath it.
        var result = reader.Read(Declaring(K("Relay"), K("Hosts"), K("Retries")), includeDeployment: true);

        var byKey = result.Entries.ToDictionary(e => e.Key);
        byKey.Should().HaveCount(6);
        byKey["Relay:Host"].Value.Should().Be("relay.example.com");
        byKey["Relay:Auth:Secret"].Redacted.Should().BeTrue();
        byKey["Hosts:0"].Value.Should().Be("a.example.com");
        byKey["Hosts:1"].Redacted.Should().BeTrue();
        byKey["Retries"].Value.Should().Be("3");
        byKey["Other:Port"].Redacted.Should().BeTrue("it was not listed, and a number is not exempt");
        Everything(result).Should().NotContain(Pass).And.NotContain("hunter2x");
    }

    [Fact]
    public void A_listed_key_nobody_set_is_reported_with_the_source_default()
    {
        var reader = new ModuleConfigurationReader(Config());

        var result = reader.Read(Declaring(K("Host")), includeDeployment: true);

        result.Entries.Should().HaveCount(1);
        result.Entries[0].Should().Be(new ModuleConfigurationEntry("Host", null, false, "deployment", "default"));
    }

    [Fact]
    public void A_tenant_reader_sees_tenant_scope_keys_only()
    {
        var reader = new ModuleConfigurationReader(Config(
            ($"{Section}:Host", "mail.example.com"),
            ($"{Section}:From", "no-reply@example.com"),
            ($"{Section}:Internal", "internal-relay-9")));

        var result = reader.Read(
            Declaring(K("Host", ConfigurationScope.Deployment), K("From", ConfigurationScope.Tenant)),
            includeDeployment: false);

        result.Entries.Should().HaveCount(1);
        result.Entries[0].Key.Should().Be("From");
        result.Entries[0].Scope.Should().Be("tenant");
        var body = Everything(result);
        body.Should().NotContain("mail.example.com").And.NotContain("internal-relay-9");
    }

    [Fact]
    public void The_environment_beats_appsettings_and_is_reported_as_the_source()
    {
        var dir = Directory.CreateTempSubdirectory("modcfg").FullName;
        var name = "Modules__Email.Smtp__Host";
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{ "Modules": { "Email.Smtp": { "Host": "from-file.example.com", "Port": 25 } } }""");
            Environment.SetEnvironmentVariable(name, "from-env.example.com");

            var root = new ConfigurationBuilder()
                .SetBasePath(dir)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();

            var result = new ModuleConfigurationReader(root).Read(Declaring(K("Host"), K("Port")), true);

            result.Entries.Should().HaveCount(2);
            var host = result.Entries.Single(e => e.Key == "Host");
            host.Value.Should().Be("from-env.example.com");
            host.Source.Should().Be("environment");
            var port = result.Entries.Single(e => e.Key == "Port");
            port.Value.Should().Be("25");
            port.Source.Should().Be("appsettings");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_provider_it_cannot_name_reports_unknown()
    {
        var result = new ModuleConfigurationReader(Config(($"{Section}:Host", "mail.example.com")))
            .Read(Declaring(K("Host")), includeDeployment: true);

        result.Entries.Should().HaveCount(1);
        result.Entries[0].Source.Should().Be("unknown");
    }

    [Fact]
    public void A_section_the_module_names_replaces_the_default()
    {
        var reader = new ModuleConfigurationReader(Config(("Smtp:Host", "mail.example.com")));
        var entry = new ModuleCatalogueEntry(Module, 1, true)
        {
            ConfigurationView = new ModuleConfigurationView([K("Host")], Section: "Smtp"),
        };

        var result = reader.Read(entry, includeDeployment: true);

        result.Section.Should().Be("Smtp");
        result.Entries.Should().HaveCount(1);
        result.Entries[0].Value.Should().Be("mail.example.com");
    }
}
