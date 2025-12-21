using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class WorkflowPluginRegistryTests
{
    [Fact]
    public void GetAllActions_ShouldReturnAllRegisteredActions()
    {
        // Arrange
        var actions = GetTestActions();
        var registry = new WorkflowPluginRegistry(actions);

        // Act
        var result = registry.GetAllActions();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(6, result.Count);
        Assert.Contains(result, a => a.Type == "Email");
        Assert.Contains(result, a => a.Type == "SMS");
        Assert.Contains(result, a => a.Type == "Webhook");
        Assert.Contains(result, a => a.Type == "CreateTask");
        Assert.Contains(result, a => a.Type == "UpdateField");
        Assert.Contains(result, a => a.Type == "Conditional");
    }

    [Fact]
    public void GetActionMetadata_ShouldReturnCorrectMetadata()
    {
        // Arrange
        var actions = GetTestActions();
        var registry = new WorkflowPluginRegistry(actions);

        // Act
        var metadata = registry.GetActionMetadata("Email");

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Email", metadata.Type);
        Assert.Equal("Send email notifications", metadata.Description);
        Assert.Contains("To", metadata.RequiredParameters);
        Assert.Contains("Subject", metadata.RequiredParameters);
        Assert.Contains("Body", metadata.RequiredParameters);
    }

    [Fact]
    public void GetActionMetadata_WithUnknownType_ShouldReturnNull()
    {
        // Arrange
        var actions = GetTestActions();
        var registry = new WorkflowPluginRegistry(actions);

        // Act
        var metadata = registry.GetActionMetadata("UnknownAction");

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public void IsActionRegistered_WithKnownType_ShouldReturnTrue()
    {
        // Arrange
        var actions = GetTestActions();
        var registry = new WorkflowPluginRegistry(actions);

        // Act
        var result = registry.IsActionRegistered("Email");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsActionRegistered_WithUnknownType_ShouldReturnFalse()
    {
        // Arrange
        var actions = GetTestActions();
        var registry = new WorkflowPluginRegistry(actions);

        // Act
        var result = registry.IsActionRegistered("UnknownAction");

        // Assert
        Assert.False(result);
    }

    private List<IWorkflowAction> GetTestActions()
    {
        return new List<IWorkflowAction>
        {
            new MockEmailAction(),
            new MockSmsAction(),
            new MockWebhookAction(),
            new MockCreateTaskAction(),
            new MockUpdateFieldAction(),
            new MockConditionalAction()
        };
    }

    // Mock action classes for testing (simplified versions)
    [WorkflowActionMetadata(
        Description = "Send email notifications",
        RequiredParameters = new[] { "To", "Subject", "Body" },
        ExampleJson = @"{""Type"":""Email"",""Parameters"":{""To"":""test@test.com"",""Subject"":""Test"",""Body"":""Body""}}"
    )]
    private class MockEmailAction : IWorkflowAction
    {
        public string Type => "Email";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class MockSmsAction : IWorkflowAction
    {
        public string Type => "SMS";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class MockWebhookAction : IWorkflowAction
    {
        public string Type => "Webhook";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class MockCreateTaskAction : IWorkflowAction
    {
        public string Type => "CreateTask";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class MockUpdateFieldAction : IWorkflowAction
    {
        public string Type => "UpdateField";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class MockConditionalAction : IWorkflowAction
    {
        public string Type => "Conditional";
        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
            => Task.CompletedTask;
    }
}
