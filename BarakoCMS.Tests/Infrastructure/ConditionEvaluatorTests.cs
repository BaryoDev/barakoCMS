using FluentAssertions;
using Xunit;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;

namespace BarakoCMS.Tests.Infrastructure;

public class ConditionEvaluatorTests
{
    private readonly ConditionEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_EqualOperator_WithMatch_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object> { ["_eq"] = "published" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because status matches 'published'");
    }

    [Fact]
    public void Evaluate_EqualOperator_WithMismatch_ShouldReturnFalse()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object> { ["_eq"] = "published" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "draft"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeFalse("because status is 'draft' not 'published'");
    }

    [Fact]
    public void Evaluate_CurrentUserPlaceholder_WithMatch_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["author"] = user.Id.ToString()
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because $CURRENT_USER placeholder was replaced with user ID");
    }

    [Fact]
    public void Evaluate_CurrentUserPlaceholder_WithMismatch_ShouldReturnFalse()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var otherUserId = Guid.NewGuid();
        var conditions = new Dictionary<string, object>
        {
            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["author"] = otherUserId.ToString()
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeFalse("because author is a different user");
    }

    [Fact]
    public void Evaluate_InOperator_WithMatch_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object>
            {
                ["_in"] = new[] { "draft", "review", "published" }
            }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "review"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because 'review' is in the allowed statuses");
    }

    [Fact]
    public void Evaluate_InOperator_WithMismatch_ShouldReturnFalse()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object>
            {
                ["_in"] = new[] { "draft", "review" }
            }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeFalse("because 'published' is not in the allowed statuses");
    }

    [Fact]
    public void Evaluate_NotEqualOperator_WithDifferentValues_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object> { ["_ne"] = "archived" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because status is not 'archived'");
    }

    [Fact]
    public void Evaluate_NotInOperator_WithValueNotInArray_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object>
            {
                ["_nin"] = new[] { "archived", "deleted" }
            }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published"
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because 'published' is not in the excluded statuses");
    }

    [Fact]
    public void Evaluate_MissingField_ShouldReturnFalse()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published" // No 'author' field
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeFalse("because the 'author' field doesn't exist in content");
    }

    [Fact]
    public void Evaluate_MultipleConditions_AllMatch_ShouldReturnTrue()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object> { ["_eq"] = "published" },
            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "published",
            ["author"] = user.Id.ToString()
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeTrue("because both conditions match");
    }

    [Fact]
    public void Evaluate_MultipleConditions_OneDoesNotMatch_ShouldReturnFalse()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };
        var conditions = new Dictionary<string, object>
        {
            ["status"] = new Dictionary<string, object> { ["_eq"] = "published" },
            ["author"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" }
        };
        var contentData = new Dictionary<string, object>
        {
            ["status"] = "draft", // Doesn't match
            ["author"] = user.Id.ToString()
        };

        // Act
        var result = _evaluator.Evaluate(conditions, contentData, user);

        // Assert
        result.Should().BeFalse("because status condition fails (draft != published)");
    }
}
