using Xunit;
using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using System.Text.Json;

namespace BarakoCMS.Tests;

/// <summary>
/// Guards the field-type contract (roadmap F.1 + F.2): the allowed set is defined
/// once in <see cref="FieldTypeRegistry"/>, every validator agrees with it (no
/// drift), and the new validation-shaped types actually validate their format.
/// </summary>
public class FieldTypeRegistryTests
{
    private readonly ContentTypeValidatorService _typeValidator = new();

    // ---- F.2: parity — no validator may drift from the registry ----------------

    [Fact]
    public void EveryRegistryType_IsAcceptedByBothTypeNameValidators()
    {
        foreach (var type in FieldTypeRegistry.AllowedTypeNames)
        {
            // Core static helper
            FieldTypeValidator.IsValidFieldType(type)
                .Should().BeTrue($"FieldTypeValidator should accept registry type '{type}'");

            // The runtime content-type-definition validator.
            //
            // A reference needs one thing more than a name and a type, because a reference with no
            // target is an untyped uuid. That is the only type where the definition validator asks
            // for more than the registry does, and the parity being checked here is that the two
            // agree on which type NAMES are allowed, not that every type is declarable with the same
            // fields filled in.
            var definition = new FieldDefinition { Name = "Field", Type = type };
            if (string.Equals(type, "reference", StringComparison.OrdinalIgnoreCase))
                definition.ReferenceType = "sometype";

            var (isValid, errors) = _typeValidator.Validate(
                "sample", "Sample",
                new List<FieldDefinition> { definition });

            isValid.Should().BeTrue(
                $"ContentTypeValidatorService should accept registry type '{type}' but said: {string.Join("; ", errors)}");
        }
    }

    [Theory]
    [InlineData("varchar")]
    [InlineData("double")]
    [InlineData("blob")]      // planned, not yet accepted
    public void UnknownType_IsRejectedEverywhere(string type)
    {
        FieldTypeRegistry.IsKnownType(type).Should().BeFalse();
        FieldTypeValidator.IsValidFieldType(type).Should().BeFalse();

        var (isValid, _) = _typeValidator.Validate(
            "sample", "Sample",
            new List<FieldDefinition> { new() { Name = "Field", Type = type } });
        isValid.Should().BeFalse($"'{type}' is not in the registry");
    }

    [Theory]
    [InlineData("text")]     // alias of string
    [InlineData("number")]   // alias of int
    [InlineData("integer")]  // alias of int
    [InlineData("boolean")]  // alias of bool
    public void HistoricalAliases_StillResolve(string alias)
    {
        FieldTypeRegistry.IsKnownType(alias).Should().BeTrue($"'{alias}' must keep working so existing content types don't break");
    }

    // ---- F.1: the new validation-shaped types have real format checks ----------

    [Theory]
    [InlineData("email", "arnel@baryo.dev", true)]
    [InlineData("email", "a@b.co", true)]
    [InlineData("email", "not-an-email", false)]
    [InlineData("email", "missing@dot", false)]
    [InlineData("url", "https://baryo.dev", true)]
    [InlineData("url", "http://x.test/path?q=1", true)]
    [InlineData("url", "ftp://x.test", false)]
    [InlineData("url", "notaurl", false)]
    [InlineData("slug", "hello-world", true)]
    [InlineData("slug", "post-123", true)]
    [InlineData("slug", "Hello_World", false)]
    [InlineData("slug", "hello--world", false)]
    [InlineData("uuid", "6f9619ff-8b86-d011-b42d-00cf4fc964ff", true)]
    [InlineData("uuid", "not-a-guid", false)]
    [InlineData("time", "13:45", true)]
    [InlineData("time", "09:00:30", true)]
    [InlineData("time", "notatime", false)]
    [InlineData("richtext", "<p>hi</p>", true)]
    [InlineData("markdown", "# heading", true)]
    public void ValidationShapedTypes_CheckFormat(string type, string value, bool expected)
    {
        FieldTypeRegistry.IsValidValue(type, value).Should().Be(expected);
    }

    [Theory]
    [InlineData(12.50, true)]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    public void Money_AcceptsNumbers(object value, bool expected)
    {
        FieldTypeRegistry.IsValidValue("money", value).Should().Be(expected);
    }

    [Fact]
    public void Money_AcceptsNumericStringButRejectsWords()
    {
        FieldTypeRegistry.IsValidValue("money", "19.99").Should().BeTrue();
        FieldTypeRegistry.IsValidValue("money", "cheap").Should().BeFalse();
    }

    [Fact]
    public void Json_AcceptsObjectsAndArrays_ButNotScalars()
    {
        FieldTypeRegistry.IsValidValue("json", new Dictionary<string, object> { ["k"] = "v" }).Should().BeTrue();
        FieldTypeRegistry.IsValidValue("json", new List<int> { 1, 2, 3 }).Should().BeTrue();
        FieldTypeRegistry.IsValidValue("json", "just a string").Should().BeFalse();
    }

    [Fact]
    public void ValidationShapedTypes_WorkThroughJsonElement()
    {
        // Values arriving from the API are JsonElements, not CLR strings.
        var email = JsonDocument.Parse("\"arnel@baryo.dev\"").RootElement;
        var badEmail = JsonDocument.Parse("\"nope\"").RootElement;
        var money = JsonDocument.Parse("42.50").RootElement;

        FieldTypeRegistry.IsValidValue("email", email).Should().BeTrue();
        FieldTypeRegistry.IsValidValue("email", badEmail).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("money", money).Should().BeTrue();
    }

    [Fact]
    public void EditorHint_IsProvidedForNewTypes()
    {
        FieldTypeRegistry.EditorHintFor("email").Should().Be("email");
        FieldTypeRegistry.EditorHintFor("money").Should().Be("money");
        FieldTypeRegistry.EditorHintFor("richtext").Should().Be("richtext");
        // Unknown types fall back to a plain text input.
        FieldTypeRegistry.EditorHintFor("mystery").Should().Be("text");
    }
}
