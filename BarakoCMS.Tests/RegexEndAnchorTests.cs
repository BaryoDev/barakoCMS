using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Collections;
using barakoCMS.Features.Connectors;
using barakoCMS.Features.Settings.Locales;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Migrations;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.ExternalAuth;
using BarakoCMS.Files.Features.StorageSettings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A whole-string check ends with <c>\z</c>, not <c>$</c>: in .NET <c>$</c> also matches before a
/// final line feed, so "name\n" would pass. Each case here is a valid value that is accepted and the
/// same value plus a line feed that is refused.
/// </summary>
public class RegexEndAnchorValidatorTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("en\n", false)]
    public void LocaleCode(string code, bool accepted)
    {
        var result = new UpdateLocaleSettingsValidator().Validate(
            new UpdateLocaleSettingsRequest { DefaultLocale = code, Locales = [code] });

        result.Errors.Any(e => e.PropertyName.StartsWith("Locales", StringComparison.Ordinal)).Should().Be(!accepted);
    }

    [Theory]
    [InlineData("my-sync", true)]
    [InlineData("my-sync\n", false)]
    public void CollectionSyncSlug(string slug, bool accepted)
    {
        var result = new SaveCollectionSyncValidator().Validate(new SaveCollectionSyncRequest { Slug = slug });

        result.Errors.Any(e => e.PropertyName == "Slug").Should().Be(!accepted);
    }

    [Theory]
    [InlineData("us-east-1", true)]
    [InlineData("us-east-1\n", false)]
    public void S3Region(string region, bool accepted)
    {
        var result = new StorageSettingsValidator().Validate(new StorageSettingsRequest
        {
            Provider = "s3",
            S3 = new S3SettingsRequest { Bucket = "b", Region = region, AccessKey = "k" },
        });

        result.Errors.Any(e => e.PropertyName.EndsWith("Region", StringComparison.Ordinal)).Should().Be(!accepted);
    }

    [Theory]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("abcdefghijklmnop\n", false)]
    public void IdTokenNonce(string nonce, bool accepted)
    {
        var result = new OidcIdTokenGrantValidator().Validate(
            new OidcIdTokenGrantRequest { IdToken = "t", Nonce = nonce });

        result.Errors.Any(e => e.PropertyName == "Nonce").Should().Be(!accepted);
    }
}

public class RegexEndAnchorRegistryTests
{
    private sealed class TriggerModule(string name) : IBarakoModule
    {
        public string Name => "regex-anchor";
        public void ConfigureWorkflowTriggers(IModuleWorkflowTriggers triggers) => triggers.Add(name, "something happened");
    }

    private sealed class TopicModule(string name) : IBarakoModule
    {
        public string Name => "regex-anchor";
        public void ConfigureTopics(IModuleTopics topics) => topics.Add(name, "read_content");
    }

    [Fact]
    public void WorkflowTriggerNameWithoutLineFeedIsRegistered() =>
        new WorkflowTriggerRegistry([new TriggerModule("forms.submitted")])
            .IsModuleTrigger("forms.submitted").Should().BeTrue();

    [Fact]
    public void WorkflowTriggerNameWithLineFeedIsRefused()
    {
        var act = () => new WorkflowTriggerRegistry([new TriggerModule("forms.submitted\n")]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TopicNameWithoutLineFeedIsRegistered() =>
        new TopicRegistry([new TopicModule("forms.submitted")]).Find("forms.submitted").Should().NotBeNull();

    [Fact]
    public void TopicNameWithLineFeedIsRefused()
    {
        var act = () => new TopicRegistry([new TopicModule("forms.submitted\n")]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ContentTypeTopicWithLineFeedIsRefused()
    {
        var type = new ContentTypeDefinition { RealtimeTopic = "forms.submitted\n" };

        var refusal = await RealtimeTopicRules.RefusalAsync(type, null!, null!, TestContext.Current.CancellationToken);

        refusal.Should().Contain("must be lower case words");
    }

    [Theory]
    [InlineData("k1", false)]
    [InlineData("k1\n", true)]
    public void KeyRingKeyId(string keyId, bool refused)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Ring:Keys:{keyId}"] = new string('x', KeyRing.MinimumKeyLength),
        }).Build();

        var act = () => KeyRing.FromConfiguration(config, "Ring", legacyMaterial: null, prefixUnkeyedWrites: false);

        if (refused)
        {
            act.Should().Throw<InvalidOperationException>().WithMessage("*key id*");
        }
        else
        {
            act.Should().NotThrow();
        }
    }
}

public class RegexEndAnchorValueTests
{
    [Theory]
    [InlineData("hero.list", true)]
    [InlineData("hero.list\n", false)]
    public void BlockDataSourceId(string id, bool accepted) => BlockDataSources.IsValidId(id).Should().Be(accepted);

    [Theory]
    [InlineData("out_1", true)]
    [InlineData("out_1\n", false)]
    public void ActionOutputName(string name, bool accepted) => ActionOutputs.IsValidName(name).Should().Be(accepted);

    [Theory]
    [InlineData("actions.1.out_1", true)]
    [InlineData("actions.1.out_1\n", false)]
    public void ActionOutputReference(string name, bool accepted) => ActionOutputs.IsReference(name).Should().Be(accepted);

    [Theory]
    [InlineData("my-slug", true)]
    [InlineData("my-slug\n", false)]
    public void Slug(string slug, bool accepted) => Slugs.IsValid(slug).Should().Be(accepted);

    [Theory]
    [InlineData("acmeclub", true)]
    [InlineData("acmeclub\n", false)]
    public void TenantHandle(string handle, bool accepted) => TenantHandles.IsValidHandle(handle).Should().Be(accepted);

    [Theory]
    [InlineData("MyField", true)]
    [InlineData("MyField\n", false)]
    public void FieldName(string name, bool accepted) => FieldTypeValidator.IsValidFieldName(name).Should().Be(accepted);

    [Theory]
    [InlineData("email", "a@b.co", true)]
    [InlineData("email", "a@b.co\n", false)]
    [InlineData("slug", "my-slug", true)]
    [InlineData("slug", "my-slug\n", false)]
    public void FieldValue(string type, string value, bool accepted) =>
        FieldTypeRegistry.IsValidValue(type, value).Should().Be(accepted);

    [Theory]
    [InlineData("hero", true)]
    [InlineData("hero\n", false)]
    public void BlockName(string name, bool accepted)
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        var errors = BlockFields.SchemaErrors(new Dictionary<string, JsonElement> { [name] = schema });

        errors.Any(e => e.StartsWith("Block name", StringComparison.Ordinal)).Should().Be(!accepted);
    }

    [Fact]
    public void FlowCookieWithoutLineFeedIsRead()
    {
        var cookie = OidcFlow.New().ToCookie();

        OidcFlow.FromCookie(cookie).Should().NotBeNull();
        OidcFlow.FromCookie(cookie + "\n").Should().BeNull();
    }

    [Theory]
    [InlineData("1.2.3/0001_start.sql", true)]
    [InlineData("1.2.3/0001_start.sql\n", false)]
    public void ShippedMigrationPath(string path, bool accepted)
    {
        var act = () => ShippedMigrations.Parse("core", path, "select 1;");

        if (accepted)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData("items[0].name", 1)]
    [InlineData("items[0].name\n", 0)]
    public void SyncArrayPathKey(string key, int expected)
    {
        var row = new Dictionary<string, string> { [key] = "x" };

        SyncRules.Values(row, "items[].name").Should().HaveCount(expected);
    }

    [Theory]
    [InlineData("my-connector", false)]
    [InlineData("my-connector\n", true)]
    public void ConnectorSlug(string slug, bool refused)
    {
        var error = ConnectorRules.Check(new SaveConnectorRequest { Name = "n", Slug = slug });

        (error is not null && error.StartsWith("Slug must be", StringComparison.Ordinal)).Should().Be(refused);
    }
}
