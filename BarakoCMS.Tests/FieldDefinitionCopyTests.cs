using System.Reflection;
using FluentAssertions;
using barakoCMS.Features.Public.Events;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>ContentChangeListener</c> copies a field definition member by member to change its
/// sensitivity. A property added to <see cref="FieldDefinition"/> and not to that copy would be
/// dropped with nothing said, so this walks every public property instead of naming them.
/// </summary>
public class FieldDefinitionCopyTests
{
    private static readonly PropertyInfo[] Properties =
        typeof(FieldDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>A value for the property that is not what a new <see cref="FieldDefinition"/> holds.</summary>
    private static object ValueFor(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string)) return "set-" + property.Name;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 7;
        if (type == typeof(object)) return "a default";
        if (type == typeof(SensitivityLevel)) return SensitivityLevel.Sensitive;
        if (type == typeof(FieldMask)) return FieldMask.Last4;
        if (type == typeof(List<string>)) return new List<string> { "Editor" };
        if (type == typeof(List<FieldOption>)) return new List<FieldOption> { new() { Value = "A", Label = "A" } };
        if (type == typeof(Dictionary<string, object>)) return new Dictionary<string, object> { ["min"] = 1 };

        throw new Xunit.Sdk.XunitException(
            $"FieldDefinition.{property.Name} is a {property.PropertyType.Name}, which this test cannot fill in. "
            + "Add a value for it here, and copy the property in ContentChangeListener.WithSensitivity.");
    }

    [Fact]
    public void Changing_a_fields_sensitivity_keeps_every_other_member_of_the_definition()
    {
        Properties.Should().NotBeEmpty();

        var untouched = new FieldDefinition();
        var source = new FieldDefinition();
        foreach (var property in Properties)
        {
            property.CanWrite.Should().BeTrue($"{property.Name} has to be settable to be copied");
            var value = ValueFor(property);
            Equals(value, property.GetValue(untouched)).Should().BeFalse(
                $"{property.Name} has to hold something other than its default, or a copy that skips it looks right");
            property.SetValue(source, value);
        }

        var copy = ContentChangeListener.WithSensitivity(source, SensitivityLevel.Hidden);

        copy.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        foreach (var property in Properties.Where(p => p.Name != nameof(FieldDefinition.Sensitivity)))
        {
            Equals(property.GetValue(copy), property.GetValue(source)).Should().BeTrue(
                $"FieldDefinition.{property.Name} is not copied by ContentChangeListener.WithSensitivity");
        }
    }
}
