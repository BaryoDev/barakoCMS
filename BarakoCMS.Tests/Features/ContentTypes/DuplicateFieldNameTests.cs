using barakoCMS.Features.ContentType.Blueprints;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A type cannot declare two fields whose names differ only in case, because every reader finds a
/// field by its name ignoring case.
/// </summary>
/// <remarks>
/// Create, the add-field route and a blueprint all ask the same validator, so the validator is what
/// these call, plus the blueprint catalog once to show it reaches the files.
/// </remarks>
public class DuplicateFieldNameTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"barako-dupfield-{Guid.NewGuid():N}");

    public DuplicateFieldNameTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static FieldDefinition Field(string name) => new() { Name = name, DisplayName = name, Type = "string" };

    [Fact]
    public void Two_fields_with_the_same_name_are_refused()
    {
        var (valid, errors) = new ContentTypeValidatorService().Validate("article", "Article", [Field("Title"), Field("Title")]);

        valid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("'Title' is declared more than once"));
    }

    [Fact]
    public void Two_fields_whose_names_differ_only_in_case_are_refused()
    {
        var (valid, errors) = new ContentTypeValidatorService().Validate("article", "Article", [Field("Title"), Field("TITLE")]);

        valid.Should().BeFalse();
        errors.Should().ContainSingle(e => e.Contains("declared more than once, ignoring case") && e.Contains("'TITLE'"));
    }

    [Fact]
    public void Distinct_names_are_accepted()
    {
        var (valid, errors) = new ContentTypeValidatorService().Validate("article", "Article", [Field("Title"), Field("Subtitle")]);

        errors.Should().BeEmpty();
        valid.Should().BeTrue();
    }

    [Fact]
    public void A_pair_already_stored_together_does_not_block_a_change_but_a_new_field_joining_it_does()
    {
        var first = Field("Title");
        var second = Field("TITLE");
        var validator = new ContentTypeValidatorService();

        var (unchanged, unchangedErrors) = validator.Validate("article", "Article", [first, second, Field("Body")], [first, second]);
        unchangedErrors.Should().BeEmpty();
        unchanged.Should().BeTrue();

        var (joined, joinedErrors) = validator.Validate("article", "Article", [first, Field("Title")], [first]);
        joined.Should().BeFalse();
        joinedErrors.Should().ContainSingle(e => e.Contains("declared more than once"));
    }

    [Fact]
    public void A_blueprint_declaring_a_field_twice_is_listed_with_the_reason()
    {
        File.WriteAllText(Path.Combine(_directory, "twice.json"), """
            {
              "name": "twice",
              "contentTypes": [
                {
                  "name": "twice-entry",
                  "displayName": "Entry",
                  "fields": [
                    { "name": "Note", "displayName": "Note", "type": "string" },
                    { "name": "note", "displayName": "Note again", "type": "string" }
                  ]
                }
              ]
            }
            """);

        var catalog = new BlueprintCatalog(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [BlueprintCatalog.PathKey] = _directory })
                .Build(),
            new ContentTypeValidatorService(),
            NullLogger<BlueprintCatalog>.Instance);

        var entry = catalog.Find("twice");

        entry.Should().NotBeNull();
        entry!.IsValid.Should().BeFalse();
        entry.Errors.Should().Contain(e => e.Contains("'Note' is declared more than once"));
    }
}
