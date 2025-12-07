using FluentAssertions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Models;

public class RoleTests
{
    [Fact]
    public void Role_WithPermissions_ShouldStoreCorrectly()
    {
        // Arrange & Act
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Content Editor",
            Description = "Can edit own content",
            Permissions = new List<ContentTypePermission>
            {
                new ContentTypePermission
                {
                    ContentTypeSlug = "article",
                    Update = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
                        }
                    }
                }
            }
        };

        // Assert
        role.Id.Should().NotBeEmpty();
        role.Name.Should().Be("Content Editor");
        role.Permissions.Should().HaveCount(1);
        role.Permissions[0].ContentTypeSlug.Should().Be("article");
        role.Permissions[0].Update.Enabled.Should().BeTrue();
        role.Permissions[0].Update.Conditions.Should().ContainKey("author");
    }

    [Fact]
    public void Role_WithMultipleContentTypes_ShouldStoreAll()
    {
        // Arrange & Act
        var role = new Role
        {
            Name = "Multi-Type Editor",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = "article" },
                new() { ContentTypeSlug = "product" },
                new() { ContentTypeSlug = "page" }
            }
        };

        // Assert
        role.Permissions.Should().HaveCount(3);
        role.Permissions.Select(p => p.ContentTypeSlug).Should().Contain(new[] { "article", "product", "page" });
    }

    [Fact]
    public void Role_WithSystemCapabilities_ShouldStoreCapabilities()
    {
        // Arrange & Act
        var role = new Role
        {
            Name = "Admin",
            SystemCapabilities = new List<string> { "manage_users", "view_analytics", "export_data" }
        };

        // Assert
        role.SystemCapabilities.Should().HaveCount(3);
        role.SystemCapabilities.Should().Contain("manage_users");
    }

    [Fact]
    public void Role_DefaultState_ShouldHaveEmptyPermissions()
    {
        // Arrange & Act
        var role = new Role();

        // Assert
        role.Permissions.Should().NotBeNull();
        role.Permissions.Should().BeEmpty();
        role.SystemCapabilities.Should().NotBeNull();
        role.SystemCapabilities.Should().BeEmpty();
    }

    [Fact]
    public void Role_Name_ShouldNotBeNullOrEmpty()
    {
        // Arrange & Act
        var role = new Role
        {
            Name = "Valid Role"
        };

        // Assert
        role.Name.Should().NotBeNullOrEmpty();
    }
}
