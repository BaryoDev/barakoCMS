using FluentAssertions;
using Xunit;
using Moq;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;

namespace BarakoCMS.Tests.Infrastructure;

public class PermissionResolverTests
{
    [Fact]
    public async Task CanPerformAction_SingleRoleGrants_ShouldAllow()
    {
        // Arrange
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Editor",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = "article",
                    Update = new PermissionRule { Enabled = true }
                }
            }
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleIds = new List<Guid> { role.Id }
        };

        // Mock IDocumentSession
        var mockSession = new Mock<IDocumentSession>();
        mockSession.Setup(s => s.LoadAsync<Role>(role.Id, default))
            .ReturnsAsync(role);

        // Mock IConditionEvaluator (not needed for this test)
        var mockConditionEvaluator = new Mock<IConditionEvaluator>();

        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update");

        // Assert
        result.Should().BeTrue("because the role grants update permission");
    }

    [Fact]
    public async Task CanPerformAction_MultipleRoles_AllGrant_ShouldAllow()
    {
        // Arrange
        var role1 = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Editor",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = "article", Update = new PermissionRule { Enabled = true } }
            }
        };

        var role2 = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Contributor",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = "article", Update = new PermissionRule { Enabled = true } }
            }
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleIds = new List<Guid> { role1.Id, role2.Id }
        };

        var mockSession = new Mock<IDocumentSession>();
        mockSession.Setup(s => s.LoadAsync<Role>(role1.Id, default)).ReturnsAsync(role1);
        mockSession.Setup(s => s.LoadAsync<Role>(role2.Id, default)).ReturnsAsync(role2);

        var mockConditionEvaluator = new Mock<IConditionEvaluator>();
        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update");

        // Assert
        result.Should().BeTrue("because ALL roles grant update permission");
    }

    [Fact]
    public async Task CanPerformAction_MultipleRoles_OneDenies_ShouldDeny_MostRestrictive()
    {
        // Arrange - This is the KEY test for "Most Restrictive" logic
        var editorRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Editor",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = "article", Update = new PermissionRule { Enabled = true } }
            }
        };

        var viewerRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Viewer",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = "article", Update = new PermissionRule { Enabled = false } }
            }
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleIds = new List<Guid> { editorRole.Id, viewerRole.Id }
        };

        var mockSession = new Mock<IDocumentSession>();
        mockSession.Setup(s => s.LoadAsync<Role>(editorRole.Id, default)).ReturnsAsync(editorRole);
        mockSession.Setup(s => s.LoadAsync<Role>(viewerRole.Id, default)).ReturnsAsync(viewerRole);

        var mockConditionEvaluator = new Mock<IConditionEvaluator>();
        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update");

        // Assert
        result.Should().BeFalse("because ONE role denies (Most Restrictive = ALL must allow)");
    }

    [Fact]
    public async Task CanPerformAction_NoRoles_ShouldDeny()
    {
        // Arrange
        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleIds = new List<Guid>() // Empty roles
        };

        var mockSession = new Mock<IDocumentSession>();
        var mockConditionEvaluator = new Mock<IConditionEvaluator>();
        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update");

        // Assert
        result.Should().BeFalse("because user has no roles");
    }

    [Fact]
    public async Task CanPerformAction_WithCondition_Matches_ShouldAllow()
    {
        // Arrange
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Editor",
            Permissions = new List<ContentTypePermission>
            {
                new()
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

        var user = new User
        {
            Id = Guid.NewGuid(),
            RoleIds = new List<Guid> { role.Id }
        };

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "article",
            Data = new Dictionary<string, object>
            {
                ["author"] = user.Id.ToString()
            }
        };

        var mockSession = new Mock<IDocumentSession>();
        mockSession.Setup(s => s.LoadAsync<Role>(role.Id, default)).ReturnsAsync(role);

        // Mock condition evaluator to return TRUE (condition matches)
        var mockConditionEvaluator = new Mock<IConditionEvaluator>();
        mockConditionEvaluator
            .Setup(e => e.Evaluate(
                It.IsAny<Dictionary<string, object>>(),
                content.Data,
                user))
            .Returns(true);

        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update", content);

        // Assert
        result.Should().BeTrue("because condition matches (author == current user)");
        mockConditionEvaluator.Verify(e => e.Evaluate(
            It.IsAny<Dictionary<string, object>>(),
            content.Data,
            user), Times.Once);
    }

    [Fact]
    public async Task CanPerformAction_WithCondition_DoesNotMatch_ShouldDeny()
    {
        // Arrange
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Editor",
            Permissions = new List<ContentTypePermission>
            {
                new()
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

        var user = new User { Id = Guid.NewGuid(), RoleIds = new List<Guid> { role.Id } };
        var otherUserId = Guid.NewGuid();

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "article",
            Data = new Dictionary<string, object>
            {
                ["author"] = otherUserId.ToString() // Different author
            }
        };

        var mockSession = new Mock<IDocumentSession>();
        mockSession.Setup(s => s.LoadAsync<Role>(role.Id, default)).ReturnsAsync(role);

        // Mock condition evaluator to return FALSE (condition doesn't match)
        var mockConditionEvaluator = new Mock<IConditionEvaluator>();
        mockConditionEvaluator
            .Setup(e => e.Evaluate(
                It.IsAny<Dictionary<string, object>>(),
                content.Data,
                user))
            .Returns(false);

        var resolver = new PermissionResolver(mockSession.Object, mockConditionEvaluator.Object);

        // Act
        var result = await resolver.CanPerformActionAsync(user, "article", "update", content);

        // Assert
        result.Should().BeFalse("because condition fails (author != current user)");
        mockConditionEvaluator.Verify(e => e.Evaluate(
            It.IsAny<Dictionary<string, object>>(),
            content.Data,
            user), Times.Once);
    }
}
