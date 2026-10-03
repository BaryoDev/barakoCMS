using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using System.Text.Json;

namespace BarakoCMS.Tests;

/// <summary>
/// The <c>file</c> field type as the registry and the type validator see it, with no host.
/// </summary>
public class FileFieldTypeTests
{
    private const string Id = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    private readonly ContentTypeValidatorService _typeValidator = new();

    [Fact]
    public void File_is_a_field_type_with_its_own_editor_hint()
    {
        FieldTypeRegistry.IsKnownType("file").Should().BeTrue();
        FieldTypeRegistry.IsKnownType("FILE").Should().BeTrue("type names are matched without case");
        FieldTypeRegistry.EditorHintFor("file").Should().Be("file");
        FieldTypeRegistry.IsNumericType("file").Should().BeFalse();
    }

    [Theory]
    [InlineData(Id, true)]
    [InlineData("6F9619FF-8B86-D011-B42D-00CF4FC964FF", true)]
    [InlineData("6f9619ff8b86d011b42d00cf4fc964ff", false)]
    [InlineData("{6f9619ff-8b86-d011-b42d-00cf4fc964ff}", false)]
    [InlineData("/api/public/files/6f9619ff-8b86-d011-b42d-00cf4fc964ff", false)]
    [InlineData("https://cdn.example.com/photo.png", false)]
    [InlineData("", false)]
    public void A_file_value_is_an_id_written_with_hyphens_and_nothing_else(string value, bool expected)
    {
        FieldTypeRegistry.IsValidValue("file", value).Should().Be(expected);

        var element = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;
        FieldTypeRegistry.IsValidValue("file", element).Should().Be(expected, "a request body arrives as a JsonElement");
    }

    [Fact]
    public void A_file_value_that_is_not_text_is_refused()
    {
        FieldTypeRegistry.IsValidValue("file", 5).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("file", Guid.Parse(Id)).Should().BeFalse("an entry stores text");
        FieldTypeRegistry.IsValidValue("file", new Dictionary<string, object> { ["id"] = Id }).Should().BeFalse(
            "the object delivery answers with is not what a write takes");
    }

    [Fact]
    public void A_type_may_declare_a_file_field_with_no_other_member_set()
    {
        var (isValid, errors) = _typeValidator.Validate(
            "gallery", "Gallery", [new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" }]);

        isValid.Should().BeTrue(string.Join("; ", errors));
    }

    [Fact]
    public void A_file_field_takes_the_image_editor_and_no_list_editor()
    {
        var image = new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file", Editor = "image" };
        _typeValidator.Validate("gallery", "Gallery", [image]).IsValid.Should().BeTrue();

        var blocks = new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file", Editor = "blocks" };
        var refused = _typeValidator.Validate("gallery", "Gallery", [blocks]);
        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().ContainSingle().Which.Should().Contain("Cover").And.Contain("blocks");

        // A string or url field keeps the hint it could already carry.
        FieldPresentation.Editors.Single(e => e.Name == "image").FieldTypes
            .Should().Equal(["url", "string", "file"]);
    }

    [Theory]
    [InlineData("referenceType")]
    [InlineData("options")]
    [InlineData("currency")]
    [InlineData("minLength")]
    public void A_file_field_takes_no_member_or_rule_that_belongs_to_another_type(string member)
    {
        var field = new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" };
        switch (member)
        {
            case "referenceType": field.ReferenceType = "author"; break;
            case "options": field.Options = [new FieldOption { Value = "a", Label = "A" }]; break;
            case "currency": field.Currency = "USD"; break;
            case "minLength": field.ValidationRules["minLength"] = 3; break;
        }

        var (isValid, errors) = _typeValidator.Validate("gallery", "Gallery", [field]);

        isValid.Should().BeFalse();
        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(e => e.Contains("Cover"));
    }
}
