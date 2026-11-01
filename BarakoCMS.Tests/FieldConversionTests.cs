using barakoCMS.Core.Validation;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>What a convert does to the field definition itself.</summary>
public class FieldConversionTests
{
    [Fact]
    public void A_converted_field_does_not_keep_the_default_of_its_old_type()
    {
        var field = new FieldDefinition { Name = "age", DisplayName = "Age", Type = "string", DefaultValue = "abc" };
        var change = new FieldChange { Field = "age", Kind = FieldChangeKind.Convert, ToType = "int" };

        FieldConversion.Retype(field, change);

        field.Type.Should().Be("int");
        field.DefaultValue.Should().BeNull();
    }
}
