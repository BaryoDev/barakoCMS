using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class WorkflowSchemaValidatorTests
{
    private readonly Mock<IWorkflowPluginRegistry> _mockRegistry;
    private readonly WorkflowSchemaValidator _validator;

    public WorkflowSchemaValidatorTests()
    {
        _mockRegistry = new Mock<IWorkflowPluginRegistry>();
        SetupMockRegistry();
        _validator = new WorkflowSchemaValidator(_mockRegistry.Object);
    }

    [Fact]
    public void Validate_ValidWorkflow_ShouldReturnValid()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test Workflow",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "Created",
            Conditions = new Dictionary<string, string> { { "Status", "Approved" } },
            Actions = new List<WorkflowAction>
            {
                new()
                {
                    Type = "Email",
                    Parameters = new Dictionary<string, string>
                    {
                        { "To", "admin@example.com" },
                        { "Subject", "Test" },
                        { "Body", "Test message" }
                    }
                }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_MissingName_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>
            {
                new() { Type = "Email", Parameters = new() { { "To", "test@test.com" }, { "Subject", "Test" }, { "Body", "Body" } } }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "name" && e.Message.Contains("required"));
    }

    [Fact]
    public void Validate_MissingTriggerContentType_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test",
            TriggerContentType = "",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>
            {
                new() { Type = "Email", Parameters = new() { { "To", "test@test.com" }, { "Subject", "Test" }, { "Body", "Body" } } }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "triggerContentType");
    }

    [Fact]
    public void Validate_InvalidTriggerEvent_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "InvalidEvent",
            Actions = new List<WorkflowAction>
            {
                new() { Type = "Email", Parameters = new() { { "To", "test@test.com" }, { "Subject", "Test" }, { "Body", "Body" } } }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "triggerEvent" && e.Message.Contains("must be one of"));
    }

    [Fact]
    public void Validate_NoActions_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>()
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "actions" && e.Message.Contains("At least one action is required"));
    }

    [Fact]
    public void Validate_UnknownActionType_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>
            {
                new() { Type = "UnknownAction", Parameters = new() }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "actions[0].type" && e.Message.Contains("Unknown action type"));
    }

    [Fact]
    public void Validate_MissingRequiredParameter_ShouldReturnError()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "Test",
            TriggerContentType = "PurchaseOrder",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>
            {
                new()
                {
                    Type = "Email",
                    Parameters = new Dictionary<string, string>
                    {
                        { "To", "admin@example.com" }
                        // Missing Subject and Body
                    }
                }
            }
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field.Contains("Subject"));
        Assert.Contains(result.Errors, e => e.Field.Contains("Body"));
    }

    [Fact]
    public void Validate_MultipleErrors_ShouldReturnAllErrors()
    {
        // Arrange
        var workflow = new WorkflowDefinition
        {
            Name = "",
            TriggerContentType = "",
            TriggerEvent = "",
            Actions = new List<WorkflowAction>()
        };

        // Act
        var result = _validator.Validate(workflow);

        // Assert
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 4); // name, triggerContentType, triggerEvent, actions
    }

    private void SetupMockRegistry()
    {
        _mockRegistry.Setup(r => r.IsActionRegistered("Email")).Returns(true);
        _mockRegistry.Setup(r => r.IsActionRegistered("SMS")).Returns(true);
        _mockRegistry.Setup(r => r.IsActionRegistered("Webhook")).Returns(true);
        _mockRegistry.Setup(r => r.IsActionRegistered(It.IsNotIn("Email", "SMS", "Webhook"))).Returns(false);

        _mockRegistry.Setup(r => r.GetActionMetadata("Email")).Returns(new WorkflowActionMetadata
        {
            Type = "Email",
            RequiredParameters = new List<string> { "To", "Subject", "Body" }
        });

        _mockRegistry.Setup(r => r.GetAllActions()).Returns(new List<WorkflowActionMetadata>
        {
            new() { Type = "Email" },
            new() { Type = "SMS" },
            new() { Type = "Webhook" }
        });
    }
}
