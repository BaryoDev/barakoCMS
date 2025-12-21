using Xunit;
using FluentAssertions;
using barakoCMS.Core.Validation;

namespace BarakoCMS.Tests;

/// <summary>
/// Unit tests for field type and naming validation
/// </summary>
public class ValidationTests
{
    #region FieldTypeValidator Tests
    
    [Theory]
    [InlineData("string")]
    [InlineData("int")]
    [InlineData("bool")]
    [InlineData("datetime")]
    [InlineData("decimal")]
    [InlineData("array")]
    [InlineData("object")]
    public void FieldTypeValidator_ShouldAcceptValidTypes(string type)
    {
        // Act
        var result = FieldTypeValidator.IsValidFieldType(type);
        
        // Assert
        result.Should().BeTrue($"'{type}' is a valid field type");
    }
    
    [Theory]
    [InlineData("STRING")] // Case insensitive - should work
    [InlineData("Int")]
    [InlineData("BOOL")]
    public void FieldTypeValidator_ShouldBeCaseInsensitive(string type)
    {
        // Act
        var result = FieldTypeValidator.IsValidFieldType(type);
        
        // Assert
        result.Should().BeTrue($"validation should be case-insensitive");
    }
    
    [Theory]
    [InlineData("varchar")]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("richtext")]
    [InlineData("")]
    [InlineData(null)]
    public void FieldTypeValidator_ShouldRejectInvalidTypes(string? type)
    {
        // Act
        var result = FieldTypeValidator.IsValidFieldType(type);
        
        // Assert
        result.Should().BeFalse($"'{type}' is not a valid field type");
    }
    
    [Theory]
    [InlineData("Name")]
    [InlineData("FirstName")]
    [InlineData("IsActive")]
    [InlineData("SSN")]
    [InlineData("BirthDay")]
    [InlineData("Age")]
    [InlineData("A")] // Single letter is valid
    public void FieldTypeValidator_ShouldAcceptPascalCaseNames(string fieldName)
    {
        // Act
        var result = FieldTypeValidator.IsValidFieldName(fieldName);
        
        // Assert
        result.Should().BeTrue($"'{fieldName}' is valid PascalCase");
    }
    
    [Theory]
    [InlineData("firstName")] // camelCase
    [InlineData("first_name")] // snake_case
    [InlineData("first-name")] // kebab-case
    [InlineData("first name")] // spaces
    [InlineData("1Name")] // starts with number
    [InlineData("")]
    [InlineData(null)]
    public void FieldTypeValidator_ShouldRejectNonPascalCaseNames(string? fieldName)
    {
        // Act
        var result = FieldTypeValidator.IsValidFieldName(fieldName);
        
        // Assert
        result.Should().BeFalse($"'{fieldName}' is not valid PascalCase");
    }
    
    [Fact]
    public void FieldTypeValidator_GetInvalidFieldTypes_ShouldReturnAllInvalidTypes()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            { "Name", "string" },      // Valid
            { "Age", "varchar" },       // Invalid
            { "Active", "bool" },       // Valid
            { "Price", "double" }       // Invalid
        };
        
        // Act
        var invalid = FieldTypeValidator.GetInvalidFieldTypes(fields);
        
        // Assert
        invalid.Should().HaveCount(2);
        invalid.Should().Contain(e => e.Contains("Age"));
        invalid.Should().Contain(e => e.Contains("Price"));
    }
    
    [Fact]
    public void FieldTypeValidator_GetInvalidFieldNames_ShouldReturnAllInvalidNames()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            { "Name", "string" },           // Valid
            { "first_name", "string" },     // Invalid
            { "IsActive", "bool" },         // Valid
            { "birth-day", "datetime" }     // Invalid
        };
        
        // Act
        var invalid = FieldTypeValidator.GetInvalidFieldNames(fields);
        
        // Assert
        invalid.Should().HaveCount(2);
        invalid.Should().Contain(e => e.Contains("first_name"));
        invalid.Should().Contain(e => e.Contains("birth-day"));
    }
    
    #endregion
    
    #region ContentDataValidator Tests
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingStringType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" }
        };
        var fields = new Dictionary<string, string>
        {
            { "Name", "string" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingIntType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Age", 25 }
        };
        var fields = new Dictionary<string, string>
        {
            { "Age", "int" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingBoolType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "IsActive", true }
        };
        var fields = new Dictionary<string, string>
        {
            { "IsActive", "bool" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingDateTimeType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "CreatedAt", DateTime.UtcNow }
        };
        var fields = new Dictionary<string, string>
        {
            { "CreatedAt", "datetime" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptDateTimeFromString()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "EventDate", "2023-12-05T10:00:00Z" }
        };
        var fields = new Dictionary<string, string>
        {
            { "EventDate", "datetime" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingDecimalType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Price", 99.99m }
        };
        var fields = new Dictionary<string, string>
        {
            { "Price", "decimal" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingArrayType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Tags", new List<string> { "tech", "cms" } }
        };
        var fields = new Dictionary<string, string>
        {
            { "Tags", "array" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAcceptMatchingObjectType()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Metadata", new Dictionary<string, object> { { "key", "value" } } }
        };
        var fields = new Dictionary<string, string>
        {
            { "Metadata", "object" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldRejectMismatchedTypes()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Age", "twenty-five" } // String instead of int
        };
        var fields = new Dictionary<string, string>
        {
            { "Age", "int" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Should().Contain("Age");
        result.Errors[0].Should().Contain("int");
    }
    
    [Fact]
    public void ContentDataValidator_ShouldRejectMultipleMismatchedTypes()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Age", "twenty-five" },      // String instead of int
            { "IsActive", "yes" },         // String instead of bool
            { "Price", "expensive" }       // String instead of decimal
        };
        var fields = new Dictionary<string, string>
        {
            { "Age", "int" },
            { "IsActive", "bool" },
            { "Price", "decimal" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAllowNullValues()
    {
        // Arrange
        var data = new Dictionary<string, object?>
        {
            { "Name", null }
        };
        var fields = new Dictionary<string, string>
        {
            { "Name", "string" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldAllowMissingOptionalFields()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John" }
            // Age field is missing
        };
        var fields = new Dictionary<string, string>
        {
            { "Name", "string" },
            { "Age", "int" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeTrue();
    }
    
    [Fact]
    public void ContentDataValidator_ShouldProvideHelpfulErrorMessages()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Attended", "yes" }
        };
        var fields = new Dictionary<string, string>
        {
            { "Attended", "bool" }
        };
        
        // Act
        var result = ContentDataValidator.ValidateData(data, fields);
        
        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors[0].Should().Contain("Attended");
        result.Errors[0].Should().Contain("bool");
        result.Errors[0].Should().Contain("yes");
    }
    
    #endregion
}
