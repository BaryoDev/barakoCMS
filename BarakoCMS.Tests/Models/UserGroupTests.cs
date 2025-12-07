using FluentAssertions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Models;

public class UserGroupTests
{
    [Fact]
    public void UserGroup_WithUsers_ShouldStoreUserIds()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();

        // Act
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = "Marketing Team",
            Description = "Marketing department members",
            UserIds = new List<Guid> { userId1, userId2 }
        };

        // Assert
        group.UserIds.Should().HaveCount(2);
        group.UserIds.Should().Contain(userId1);
        group.UserIds.Should().Contain(userId2);
    }

    [Fact]
    public void UserGroup_NestedStructure_ShouldSupportParentChild()
    {
        // Arrange
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        // Act
        var parentGroup = new UserGroup
        {
            Id = parentId,
            Name = "Engineering",
            ChildGroupIds = new List<Guid> { childId }
        };

        var childGroup = new UserGroup
        {
            Id = childId,
            Name = "Frontend Team",
            ParentGroupId = parentId
        };

        // Assert
        parentGroup.ChildGroupIds.Should().Contain(childId);
        childGroup.ParentGroupId.Should().Be(parentId);
    }

    [Fact]
    public void UserGroup_DefaultState_ShouldHaveEmptyLists()
    {
        // Arrange & Act
        var group = new UserGroup();

        // Assert
        group.UserIds.Should().NotBeNull();
        group.UserIds.Should().BeEmpty();
        group.ChildGroupIds.Should().NotBeNull();
        group.ChildGroupIds.Should().BeEmpty();
        group.ParentGroupId.Should().BeNull();
    }
}
