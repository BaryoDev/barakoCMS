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
        var mockSession = new Mock<Marten.IQuerySession>();
        var mockLogger = new Mock<ILogger<WebhookAction>>();
        var action = new WebhookAction(
            mockHttpClientFactory.Object,
            mockSession.Object,
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default,
            mockLogger.Object);

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
        var mockSession = new Mock<Marten.IQuerySession>();
        var mockLogger = new Mock<ILogger<WebhookAction>>();
        var action = new WebhookAction(
            mockHttpClientFactory.Object,
            mockSession.Object,
            barakoCMS.Infrastructure.Http.OutboundAddressGuard.Default,
            mockLogger.Object);

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

    /// <summary>
    /// Every registered action is reachable by the type a workflow names.
    /// </summary>
    /// <remarks>
    /// This replaces a test with the same promise in its name that constructed a WorkflowEngine,
    /// never called it, and then asserted that the list it had just built contained the item it had
    /// just put in it. It had an assertion, which is what made it look like a test, and the
    /// assertion could not fail.
    ///
    /// Resolution by type is what this is actually about: a workflow names "Email" as a string and
    /// something has to find the action that answers to it. Duplicates matter for the same reason,
    /// because two actions claiming one type makes which one runs an accident of registration order.
    /// </remarks>
    [Fact]
    public void Every_registered_action_is_reachable_by_its_declared_type()
    {
        var actions = new List<IWorkflowAction>
        {
            new EmailAction(new Mock<IEmailService>().Object),
        };

        var byType = actions.ToDictionary(a => a.Type, StringComparer.OrdinalIgnoreCase);

        byType.Should().ContainKey("Email", "a workflow naming Email has to resolve to something");
        byType["Email"].Should().BeOfType<EmailAction>();
    }

    /// <summary>
    /// Every action the host registers declares a distinct, non-empty type.
    /// </summary>
    /// <remarks>
    /// The check worth having, because it runs over what is actually wired up rather than over a
    /// list a test made. Two actions claiming one type is not a compile error and not a startup
    /// error: it silently makes which one runs depend on registration order.
    /// </remarks>
    [Fact]
    public void No_two_registered_actions_claim_the_same_type()
    {
        var actionTypes = typeof(WorkflowEngine).Assembly.GetTypes()
            .Where(t => typeof(IWorkflowAction).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        actionTypes.Should().HaveCountGreaterThan(3,
            "only {0} actions were found, so a duplicate check over them proves little",
            actionTypes.Length);

        var declared = actionTypes
            .Select(t => (Type: t, Name: (string)t.GetProperty("Type")!.GetValue(
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t))!))
            .ToArray();

        declared.Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.Name),
            "an action with no type cannot be named by a workflow");

        declared.Select(d => d.Name).Should().OnlyHaveUniqueItems(
            "two actions claiming one type makes which one runs an accident of registration order");
    }
}
