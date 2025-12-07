using FluentAssertions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Models;

public class PermissionRuleTests
{
    [Fact]
    public void PermissionRule_WithConditions_ShouldStoreConditions()
    {
        // Arrange & Act
        var rule = new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                ["status"] = new Dictionary<string, object> { ["_in"] = new[] { "draft", "published" } }
            }
        };

        // Assert
        rule.Enabled.Should().BeTrue();
        rule.Conditions.Should().HaveCount(2);
        rule.Conditions.Should().ContainKey("author");
        rule.Conditions.Should().ContainKey("status");
    }

    [Fact]
    public void PermissionRule_Disabled_ShouldHaveEnabledFalse()
    {
        // Arrange & Act
        var rule = new PermissionRule
        {
            Enabled = false
        };

        // Assert
        rule.Enabled.Should().BeFalse();
    }

    [Fact]
    public void PermissionRule_DefaultState_ShouldBeDisabledWithNoConditions()
    {
        // Arrange & Act
        var rule = new PermissionRule();

        // Assert
        rule.Enabled.Should().BeFalse();
        rule.Conditions.Should().BeNull();
    }

    [Fact]
    public void PermissionRule_ComplexConditions_ShouldSupportNestedOperators()
    {
        // Arrange & Act
        var rule = new PermissionRule
        {
            Enabled = true,
            Conditions = new Dictionary<string, object>
            {
                ["_and"] = new[]
                {
                    new Dictionary<string, object> { ["department"] = new Dictionary<string, object> { ["_eq"] = "$USER_DEPARTMENT" } },
                    new Dictionary<string, object> { ["confidential"] = new Dictionary<string, object> { ["_eq"] = false } }
                }
            }
        };

        // Assert
        rule.Conditions.Should().ContainKey("_and");
    }

    [Fact]
    public void ContentTypePermission_AllCRUDRules_ShouldExist()
    {
        // Arrange & Act
        var permission = new ContentTypePermission
        {
            ContentTypeSlug = "article",
            Create = new PermissionRule { Enabled = true },
            Read = new PermissionRule { Enabled = true },
            Update = new PermissionRule { Enabled = true },
            Delete = new PermissionRule { Enabled = false }
        };

        // Assert
        permission.Create.Should().NotBeNull();
        permission.Read.Should().NotBeNull();
        permission.Update.Should().NotBeNull();
        permission.Delete.Should().NotBeNull();
        permission.Create.Enabled.Should().BeTrue();
        permission.Delete.Enabled.Should().BeFalse();
    }
}
