using Xunit;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BarakoCMS.Tests;

public class SensitivityTests
{
    private readonly ISensitivityService _sensitivityService;

    public SensitivityTests()
    {
        _sensitivityService = new SensitivityService();
    }

    private HttpContext CreateHttpContext(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        var identity = claims.Count > 0
            ? new ClaimsIdentity(claims, "TestAuth")
            : new ClaimsIdentity();
        var user = new ClaimsPrincipal(identity);
        return new DefaultHttpContext { User = user };
    }

    #region Document-Level Sensitivity Tests

    [Fact]
    public void Apply_ShouldHideData_WhenHiddenAndNotSuperAdmin()
    {
        // Arrange
        var content = new Content
        {
            Sensitivity = SensitivityLevel.Hidden,
            Data = new Dictionary<string, object> { { "Secret", "Value" } }
        };
        var httpContext = CreateHttpContext(); // No roles

        // Act
        _sensitivityService.Apply(content, httpContext);

        // Assert
        Assert.Empty(content.Data);
        Assert.Equal("HIDDEN", content.ContentType);
    }

    [Fact]
    public void Apply_ShouldShowData_WhenHiddenAndSuperAdmin()
    {
        // Arrange
        var content = new Content
        {
            Sensitivity = SensitivityLevel.Hidden,
            Data = new Dictionary<string, object> { { "Secret", "Value" } }
        };
        var httpContext = CreateHttpContext("SuperAdmin");

        // Act
        _sensitivityService.Apply(content, httpContext);

        // Assert
        Assert.NotEmpty(content.Data);
        Assert.Equal("Value", content.Data["Secret"]);
    }

    [Fact]
    public void Apply_ShouldHideData_WhenSensitiveAndNotAuthorized()
    {
        // Arrange
        var content = new Content
        {
            Sensitivity = SensitivityLevel.Sensitive,
            Data = new Dictionary<string, object> { { "Internal", "Data" } }
        };
        var httpContext = CreateHttpContext("Editor"); // Not SuperAdmin or HR

        // Act
        _sensitivityService.Apply(content, httpContext);

        // Assert
        Assert.Empty(content.Data);
    }

    [Fact]
    public void Apply_ShouldShowData_WhenSensitiveAndHR()
    {
        // Arrange
        var content = new Content
        {
            Sensitivity = SensitivityLevel.Sensitive,
            Data = new Dictionary<string, object> { { "Internal", "Data" } }
        };
        var httpContext = CreateHttpContext("HR");

        // Act
        _sensitivityService.Apply(content, httpContext);

        // Assert
        Assert.NotEmpty(content.Data);
        Assert.Equal("Data", content.Data["Internal"]);
    }

    #endregion

    #region Field-Level Sensitivity Tests

    [Fact]
    public void ApplyFieldSensitivity_ShouldRemoveField_WhenNotAuthorized()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "SSN", "123-45-6789" },
            { "Email", "john@example.com" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition
                {
                    Name = "SSN",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Remove,
                        AllowedRoles = new List<string> { "HR", "SuperAdmin" }
                    }
                },
                new FieldDefinition { Name = "Email" }
            }
        };

        var httpContext = CreateHttpContext("Editor"); // Not HR or SuperAdmin

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert
        Assert.Equal(2, data.Count);
        Assert.True(data.ContainsKey("Name"));
        Assert.True(data.ContainsKey("Email"));
        Assert.False(data.ContainsKey("SSN")); // SSN should be removed
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldMaskField_WhenNotAuthorized()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "BirthDate", "1990-01-15" },
            { "Email", "john@example.com" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition
                {
                    Name = "BirthDate",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Mask,
                        AllowedRoles = new List<string> { "HR" },
                        MaskValue = "****-**-**"
                    }
                },
                new FieldDefinition { Name = "Email" }
            }
        };

        var httpContext = CreateHttpContext("Editor"); // Not HR

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert
        Assert.Equal(3, data.Count);
        Assert.Equal("John Doe", data["Name"]);
        Assert.Equal("****-**-**", data["BirthDate"]); // Should be masked
        Assert.Equal("john@example.com", data["Email"]);
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldShowField_WhenAuthorized()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "SSN", "123-45-6789" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition
                {
                    Name = "SSN",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Remove,
                        AllowedRoles = new List<string> { "HR", "SuperAdmin" }
                    }
                }
            }
        };

        var httpContext = CreateHttpContext("HR"); // HR is allowed

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert
        Assert.Equal(2, data.Count);
        Assert.Equal("123-45-6789", data["SSN"]); // SSN should be visible
    }

    [Fact]
    public void ApplyFieldSensitivity_SuperAdmin_ShouldSeeAllFields()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "SSN", "123-45-6789" },
            { "Salary", 100000 }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition
                {
                    Name = "SSN",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Remove,
                        AllowedRoles = new List<string> { "HR" } // SuperAdmin not listed
                    }
                },
                new FieldDefinition
                {
                    Name = "Salary",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Mask,
                        AllowedRoles = new List<string> { "Finance" } // SuperAdmin not listed
                    }
                }
            }
        };

        var httpContext = CreateHttpContext("SuperAdmin");

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert - SuperAdmin sees everything regardless of AllowedRoles
        Assert.Equal(3, data.Count);
        Assert.Equal("123-45-6789", data["SSN"]);
        Assert.Equal(100000, data["Salary"]);
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldHandleNullContentTypeDefinition()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "SSN", "123-45-6789" }
        };

        var httpContext = CreateHttpContext("Editor");

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, null);

        // Assert - Data should be unchanged
        Assert.Equal(2, data.Count);
        Assert.Equal("123-45-6789", data["SSN"]);
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldHandleFieldsWithoutSensitivity()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Name", "John Doe" },
            { "Email", "john@example.com" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" }, // No sensitivity
                new FieldDefinition { Name = "Email" } // No sensitivity
            }
        };

        var httpContext = CreateHttpContext("Editor");

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert - All fields visible since no sensitivity defined
        Assert.Equal(2, data.Count);
        Assert.Equal("John Doe", data["Name"]);
        Assert.Equal("john@example.com", data["Email"]);
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldHandleCaseInsensitiveRoles()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "SSN", "123-45-6789" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition
                {
                    Name = "SSN",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Remove,
                        AllowedRoles = new List<string> { "hr" } // lowercase
                    }
                }
            }
        };

        var httpContext = CreateHttpContext("HR"); // uppercase

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert - Should match case-insensitively
        Assert.Single(data);
        Assert.Equal("123-45-6789", data["SSN"]);
    }

    [Fact]
    public void ApplyFieldSensitivity_ShouldUseDefaultMaskValue()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            { "Password", "secret123" }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "User",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition
                {
                    Name = "Password",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Mask,
                        AllowedRoles = new List<string> { "Admin" }
                        // MaskValue not specified, should default to "***"
                    }
                }
            }
        };

        var httpContext = CreateHttpContext("User");

        // Act
        _sensitivityService.ApplyFieldSensitivity(data, httpContext, contentTypeDef);

        // Assert
        Assert.Equal("***", data["Password"]);
    }

    #endregion

    #region Combined Document and Field-Level Tests

    [Fact]
    public void Apply_ShouldApplyBothDocumentAndFieldSensitivity()
    {
        // Arrange
        var content = new Content
        {
            ContentType = "Employee",
            Sensitivity = SensitivityLevel.Public, // Document is public
            Data = new Dictionary<string, object>
            {
                { "Name", "John Doe" },
                { "SSN", "123-45-6789" }, // But SSN field is sensitive
                { "Email", "john@example.com" }
            }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition
                {
                    Name = "SSN",
                    Sensitivity = new FieldSensitivity
                    {
                        Action = FieldSensitivityAction.Remove,
                        AllowedRoles = new List<string> { "HR" }
                    }
                },
                new FieldDefinition { Name = "Email" }
            }
        };

        var httpContext = CreateHttpContext("Editor"); // Can see document but not SSN

        // Act
        _sensitivityService.Apply(content, httpContext, contentTypeDef);

        // Assert
        Assert.Equal(2, content.Data.Count);
        Assert.True(content.Data.ContainsKey("Name"));
        Assert.True(content.Data.ContainsKey("Email"));
        Assert.False(content.Data.ContainsKey("SSN"));
    }

    [Fact]
    public void Apply_DocumentLevelHidden_ShouldTakePrecedence()
    {
        // Arrange
        var content = new Content
        {
            ContentType = "Employee",
            Sensitivity = SensitivityLevel.Hidden, // Document is hidden
            Data = new Dictionary<string, object>
            {
                { "Name", "John Doe" },
                { "SSN", "123-45-6789" }
            }
        };

        var contentTypeDef = new ContentTypeDefinition
        {
            Name = "Employee",
            Fields = new List<FieldDefinition>
            {
                new FieldDefinition { Name = "Name" },
                new FieldDefinition { Name = "SSN" } // No field sensitivity
            }
        };

        var httpContext = CreateHttpContext("Editor");

        // Act
        _sensitivityService.Apply(content, httpContext, contentTypeDef);

        // Assert - Document-level takes precedence, all data cleared
        Assert.Empty(content.Data);
        Assert.Equal("HIDDEN", content.ContentType);
    }

    #endregion
}
