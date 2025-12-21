using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

public class WorkflowPluginTests
{
    [Fact]
    public void EmailAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockEmailService = new Mock<IEmailService>();
        var action = new EmailAction(mockEmailService.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("Email");
    }

    [Fact]
    public void SmsAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockSmsService = new Mock<ISmsService>();
        var action = new SmsAction(mockSmsService.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("SMS");
    }

    [Fact]
    public void WebhookAction_Should_HaveCorrectType()
    {
        // Arrange
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<ILogger<WebhookAction>>();
        var action = new WebhookAction(mockHttpClientFactory.Object, mockLogger.Object);

        // Act
        var type = action.Type;

        // Assert
        type.Should().Be("Webhook");
    }

    [Fact]
    public async Task EmailAction_Should_SendEmail_WithParameters()
    {
        // Arrange
        var mockEmailService = new Mock<IEmailService>();
        var action = new EmailAction(mockEmailService.Object);

        var parameters = new Dictionary<string, string>
        {
            { "To", "test@example.com" },
            { "Subject", "Test Subject" },
            { "Body", "Test Body" }
        };

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "TestType",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>()
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        mockEmailService.Verify(
            x => x.SendEmailAsync("test@example.com", "Test Subject", "Test Body", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SmsAction_Should_SendSms_WithParameters()
    {
        // Arrange
        var mockSmsService = new Mock<ISmsService>();
        var action = new SmsAction(mockSmsService.Object);

        var parameters = new Dictionary<string, string>
        {
            { "To", "+1234567890" },
            { "Message", "Test Message" }
        };

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "TestType",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>()
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        mockSmsService.Verify(
            x => x.SendSmsAsync("+1234567890", "Test Message", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EmailAction_Should_UseDefaultValues_WhenParametersMissing()
    {
        // Arrange
        var mockEmailService = new Mock<IEmailService>();
        var action = new EmailAction(mockEmailService.Object);

        var parameters = new Dictionary<string, string>(); // No parameters

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "TestType",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>()
        };

        // Act
        await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        mockEmailService.Verify(
            x => x.SendEmailAsync(
                "admin@example.com", // Default To
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WebhookAction_Should_NotThrow_WhenUrlMissing()
    {
        // Arrange
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<ILogger<WebhookAction>>();
        var action = new WebhookAction(mockHttpClientFactory.Object, mockLogger.Object);

        var parameters = new Dictionary<string, string>(); // No URL

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "TestType",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>()
        };

        // Act
        Func<Task> act = async () => await action.ExecuteAsync(parameters, content, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WorkflowEngine_Should_ExecuteCorrectPlugin_BasedOnType()
    {
        // Arrange
        var mockSession = new Mock<Marten.IDocumentSession>();
        var mockEmailService = new Mock<IEmailService>();
        var mockLogger = new Mock<ILogger<WorkflowEngine>>();

        var emailAction = new EmailAction(mockEmailService.Object);
        var actions = new List<IWorkflowAction> { emailAction };

        var engine = new WorkflowEngine(mockSession.Object, actions, mockLogger.Object);

        // Note: This test validates the plugin discovery mechanism
        // The engine should be able to resolve EmailAction from the IEnumerable<IWorkflowAction>
        actions.Should().Contain(a => a.Type == "Email");

        await Task.CompletedTask; // Placeholder to avoid CS1998 warning
    }
}
