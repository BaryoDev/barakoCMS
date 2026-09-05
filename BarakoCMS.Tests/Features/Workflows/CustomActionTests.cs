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
        // Events is stubbed explicitly rather than via DefaultValue.Mock, because auto-mocking tries
        // to proxy StreamAction, which has no parameterless constructor. The action discards the
        // return value, so a loose mock returning null is enough.
        var mockSession = new Mock<IDocumentSession>();
        mockSession.SetupGet(x => x.Events).Returns(new Mock<Marten.Events.IEventStoreOperations>().Object);
        var mockLogger = new Mock<ILogger<CreateTaskAction>>();
        var action = new CreateTaskAction(mockSession.Object, mockLogger.Object, new barakoCMS.Infrastructure.Services.ContentWriter(
            mockSession.Object, new barakoCMS.Infrastructure.Services.ContentSourcingPolicyService(mockSession.Object)));

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
        var mockContentWriter = new Mock<barakoCMS.Core.Interfaces.IContentWriter>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockContentWriter.Object, mockLogger.Object);

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
        // Events is stubbed explicitly rather than via DefaultValue.Mock, because auto-mocking tries
        // to proxy StreamAction, which has no parameterless constructor. The action discards the
        // return value, so a loose mock returning null is enough.
        var mockSession = new Mock<IDocumentSession>();
        mockSession.SetupGet(x => x.Events).Returns(new Mock<Marten.Events.IEventStoreOperations>().Object);
        var mockLogger = new Mock<ILogger<CreateTaskAction>>();
        var action = new CreateTaskAction(mockSession.Object, mockLogger.Object, new barakoCMS.Infrastructure.Services.ContentWriter(
            mockSession.Object, new barakoCMS.Infrastructure.Services.ContentSourcingPolicyService(mockSession.Object)));

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

    // UpdateFieldAction's behavioural tests (data field, status, the idempotency guard for a
    // reclaimed attempt) live in UpdateFieldActionTests, against a real store: the fix reloads the
    // target through IContentWriter.AppendOptimisticAsync, which a bare IDocumentSession mock
    // cannot stand in for meaningfully.

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
        var mockContentWriter = new Mock<barakoCMS.Core.Interfaces.IContentWriter>();
        var mockLogger = new Mock<ILogger<UpdateFieldAction>>();
        var action = new UpdateFieldAction(mockSession.Object, mockContentWriter.Object, mockLogger.Object);

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
        mockEmailAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Success());

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
            x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), content, It.IsAny<CancellationToken>()),
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
        mockSmsAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Success());

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
            x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), content, It.IsAny<CancellationToken>()),
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

    /// <summary>
    /// Issue #572: a child action that fails used to be logged and dropped, so the conditional
    /// reported success even though the branch did not do what it was configured to do.
    /// </summary>
    [Fact]
    public async Task ConditionalAction_RunAsync_Should_ReportFailure_WhenAChildActionFails()
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
        mockEmailAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Failure("the target answered 503"));

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
        var result = await action.RunAsync(parameters, content, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeFalse("the only child in the branch failed");
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain("Email", "an operator needs to know which child failed");
        result.Retryable.Should().BeTrue("nothing succeeded yet, so re-running the branch resends nothing");
    }

    /// <summary>
    /// Issue #572: retrying the conditional re-runs every child from the top, because a child has no
    /// attempt or idempotency key of its own. If an earlier child already succeeded, offering a retry
    /// would resend whatever that child sent, so this must report a non-retryable failure instead.
    /// </summary>
    [Fact]
    public async Task ConditionalAction_RunAsync_Should_ReportPermanentFailure_WhenAnEarlierChildAlreadySucceeded()
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
        mockEmailAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Success());

        var mockSmsAction = new Mock<IWorkflowAction>();
        mockSmsAction.Setup(x => x.Type).Returns("SMS");
        mockSmsAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Failure("the target answered 503"));

        var availableActions = new List<IWorkflowAction> { mockEmailAction.Object, mockSmsAction.Object };

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>)))
            .Returns(availableActions);

        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var action = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        var thenActions = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string> { { "To", "test@example.com" } } },
            new { Type = "SMS", Parameters = new Dictionary<string, string> { { "To", "+1234567890" } } }
        });

        var parameters = new Dictionary<string, string>
        {
            { "Condition", "{{data.Priority}} == \"High\"" },
            { "ThenActions", thenActions }
        };

        // Act
        var result = await action.RunAsync(parameters, content, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeFalse("the SMS child failed");
        result.Retryable.Should().BeFalse(
            "the Email child already succeeded, and retrying the conditional would resend it");
    }

    /// <summary>
    /// Issue #572: the failure has to reach the run record an operator reads, not just the value
    /// <c>RunAsync</c> hands back. This drives the same <see cref="barakoCMS.Infrastructure.Services.IWorkflowDebugger"/>
    /// the engine uses, without a database, since only <c>CompleteExecutionAsync</c> touches one.
    /// </summary>
    [Fact]
    public async Task ConditionalAction_Should_RecordFailure_OnTheWorkflowExecutionLog_WhenAChildFails()
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
        mockEmailAction
            .Setup(x => x.RunAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkflowActionResult.Failure("the target answered 503"));

        var availableActions = new List<IWorkflowAction> { mockEmailAction.Object };

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>)))
            .Returns(availableActions);

        var mockLogger = new Mock<ILogger<ConditionalAction>>();
        var conditional = new ConditionalAction(mockServiceProvider.Object, mockLogger.Object);

        var thenActions = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string> { { "To", "test@example.com" } } }
        });

        var parameters = new Dictionary<string, string>
        {
            { "Condition", "{{data.Priority}} == \"High\"" },
            { "ThenActions", thenActions }
        };

        var debugger = new barakoCMS.Infrastructure.Services.WorkflowDebugger(
            new Mock<Marten.IDocumentSession>().Object,
            new Mock<ILogger<barakoCMS.Infrastructure.Services.WorkflowDebugger>>().Object);

        var log = debugger.StartExecution(Guid.NewGuid(), content.Id);
        var timer = debugger.StartAction(log, conditional.Type);

        // Act: exactly what WorkflowEngine.ExecuteActionsAsync does with the result.
        var result = await conditional.RunAsync(parameters, content, CancellationToken.None);
        if (result.Succeeded)
        {
            debugger.LogActionSuccess(log, conditional.Type, timer, parameters);
        }
        else
        {
            debugger.LogActionFailure(log, conditional.Type, timer, result.Error ?? "unknown", parameters);
        }

        // Assert
        log.Actions.Should().HaveCount(1);
        log.Actions[0].Success.Should().BeFalse("the child action failed and the run record must show it");
        log.Actions[0].ActionType.Should().Be("Conditional");
        log.Success.Should().BeFalse("a run whose conditional swallowed a failure would read as a clean success");
    }
}
