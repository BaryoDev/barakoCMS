using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using Marten;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

public class CustomActionTests
{
    [Fact]
    public void CreateTaskAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<CreateTaskAction>>();
        var action = new CreateTaskAction(mockSession.Object, mockLogger.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("CreateTask");
    }

    [Fact]
    public void UpdateFieldAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockLogger.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("UpdateField");
    }

    [Fact]
    public void ConditionalAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockServiceProvider = new Mock<IServiceProvider>();
        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var action = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("Conditional");
    }

    [Fact]
    public async Task CreateTaskAction_Should_CreateContent()
    {
        // Arrange
        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<CreateTaskAction>>();
        var action = new CreateTaskAction(mockSession.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            { "ContentType", "Task" },
            { "Status", "Draft" },
            { "Title", "Test Task" },
            { "Data.Priority", "High" }
        };

        var triggerContent = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "PurchaseOrder",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>()
        };

        // Act
        await action.ExecuteAsync(parameters, triggerContent, CancellationToken.None);

        // Assert
        mockSession.Verify(x => x.Store(It.IsAny<Content>()), Times.Once);
        mockSession.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateFieldAction_Should_UpdateDataField()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object> { { "Priority", "Low" } },
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };

        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            { "Field", "data.Priority" },
            { "Value", "High" }
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        content.Data["Priority"].Should().Be("High");
        mockSession.Verify(x => x.Store(content), Times.Once);
        mockSession.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateFieldAction_Should_UpdateStatus()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object>()
        };

        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            { "Field", "Status" },
            { "Value", "Published" }
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        content.Status.Should().Be(ContentStatus.Published);
    }

    [Fact]
    public async Task UpdateFieldAction_Should_HandleMissingField()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object>()
        };

        var mockSession = new Mock<IDocumentSession>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            { "Value", "SomeValue" }
            // Missing "Field" parameter
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert - should not throw, just log warning
        mockSession.Verify(x => x.Store(It.IsAny<Content>()), Times.Never);
    }

    [Fact]
    public async Task ConditionalAction_Should_ExecuteThenBranch_WhenConditionTrue()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { { "Priority", "High" } }
        };

        var mockEmailAction = new Mock<IWorkflowAction>();
        mockEmailAction.Setup(x => x.Type).Returns("Email");

        var availableActions = new List<IWorkflowAction> { mockEmailAction.Object };

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>)))
            .Returns(availableActions);

        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var action = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        var thenActions = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string> { { "To", "test@example.com" } } }
        });

        var parameters = new Dictionary<string, string>
        {
            { "Condition", "{{data.Priority}} == \"High\"" },
            { "ThenActions", thenActions }
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        mockEmailAction.Verify(
            x => x.ExecuteAsync(It.IsAny<Dictionary<string, string>>(), content, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConditionalAction_Should_ExecuteElseBranch_WhenConditionFalse()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object> { { "Priority", "Low" } }
        };

        var mockSmsAction = new Mock<IWorkflowAction>();
        mockSmsAction.Setup(x => x.Type).Returns("SMS");

        var availableActions = new List<IWorkflowAction> { mockSmsAction.Object };

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>)))
            .Returns(availableActions);

        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var action = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        var elseActions = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "SMS", Parameters = new Dictionary<string, string> { { "To", "+1234567890" } } }
        });

        var parameters = new Dictionary<string, string>
        {
            { "Condition", "{{data.Priority}} == \"High\"" },
            { "ElseActions", elseActions }
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        mockSmsAction.Verify(
            x => x.ExecuteAsync(It.IsAny<Dictionary<string, string>>(), content, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConditionalAction_Should_HandleMissingElseBranch()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Task",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object> { { "Priority", "Low" } }
        };

        var mockServiceProvider = new Mock<IServiceProvider>();
        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var action = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>
        {
            { "Condition", "{{data.Priority}} == \"High\"" },
            { "ThenActions", "[]" }
            // No ElseActions defined
        };

        // Act
        Func<Task> act = async () => await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert - should not throw
        await act.Should().NotThrowAsync();
    }
}
