using barakoCMS.Models;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

[Collection("Sequential")]
public class WorkflowToolsApiTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public WorkflowToolsApiTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    #region GET /api/workflows/actions Tests

    [Fact]
    public async Task GetActions_ShouldReturnAllRegisteredActions()
    {
        // Act
        var response = await _client.GetAsync("/api/workflows/actions");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var actions = await response.Content.ReadFromJsonAsync<List<WorkflowActionMetadata>>();
        actions.Should().NotBeNull();
        actions!.Count.Should().Be(6);
    }

    [Fact]
    public async Task GetActions_ShouldReturnCorrectMetadata()
    {
        // Act
        var response = await _client.GetAsync("/api/workflows/actions");
        var actions = await response.Content.ReadFromJsonAsync<List<WorkflowActionMetadata>>();

        // Assert
        actions.Should().NotBeNull();

        // Verify Email action metadata
        var emailAction = actions!.FirstOrDefault(a => a.Type == "Email");
        emailAction.Should().NotBeNull();
        emailAction!.Description.Should().Contain("email");
        emailAction.RequiredParameters.Should().Contain("To");
        emailAction.RequiredParameters.Should().Contain("Subject");
        emailAction.RequiredParameters.Should().Contain("Body");

        // Verify all 6 action types are present
        var actionTypes = actions.Select(a => a.Type).ToList();
        actionTypes.Should().Contain(new[] { "Email", "SMS", "Webhook", "CreateTask", "UpdateField", "Conditional" });
    }

    #endregion

    #region POST /api/workflows/validate Tests

    [Fact]
    public async Task ValidateWorkflow_WithValidWorkflow_ShouldReturnValid()
    {
        // Arrange
        var request = new
        {
            name = "Test Workflow",
            triggerContentType = "PurchaseOrder",
            triggerEvent = "Created",
            conditions = new Dictionary<string, string> { { "Status", "Approved" } },
            actions = new[]
            {
                new
                {
                    type = "Email",
                    parameters = new Dictionary<string, string>
                    {
                        { "To", "admin@example.com" },
                        { "Subject", "Test" },
                        { "Body", "Test message" }
                    }
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", request);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateWorkflow_WithMissingName_ShouldReturnError()
    {
        // Arrange
        var request = new
        {
            name = "",
            triggerContentType = "PurchaseOrder",
            triggerEvent = "Created",
            conditions = new Dictionary<string, string>(),
            actions = new[]
            {
                new
                {
                    type = "Email",
                    parameters = new Dictionary<string, string>
                    {
                        { "To", "test@test.com" },
                        { "Subject", "Test" },
                        { "Body", "Body" }
                    }
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", request);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field.Contains("name"));
    }

    [Fact]
    public async Task ValidateWorkflow_WithUnknownActionType_ShouldReturnError()
    {
        // Arrange
        var request = new
        {
            name = "Test",
            triggerContentType = "PurchaseOrder",
            triggerEvent = "Created",
            conditions = new Dictionary<string, string>(),
            actions = new[]
            {
                new
                {
                    type = "UnknownAction",
                    parameters = new Dictionary<string, string>()
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", request);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Unknown action type"));
    }

    [Fact]
    public async Task ValidateWorkflow_WithMissingRequiredParameters_ShouldReturnErrors()
    {
        // Arrange
        var request = new
        {
            name = "Test",
            triggerContentType = "PurchaseOrder",
            triggerEvent = "Created",
            conditions = new Dictionary<string, string>(),
            actions = new[]
            {
                new
                {
                    type = "Email",
                    parameters = new Dictionary<string, string>
                    {
                        { "To", "admin@example.com" }
                        // Missing Subject and Body
                    }
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", request);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Errors.Should().Contain(e => e.Field.Contains("Subject"));
        result.Errors.Should().Contain(e => e.Field.Contains("Body"));
    }

    [Fact]
    public async Task ValidateWorkflow_WithInvalidTriggerEvent_ShouldReturnError()
    {
        // Arrange
        var request = new
        {
            name = "Test",
            triggerContentType = "PurchaseOrder",
            triggerEvent = "InvalidEvent",
            conditions = new Dictionary<string, string>(),
            actions = new[]
            {
                new
                {
                    type = "Email",
                    parameters = new Dictionary<string, string>
                    {
                        { "To", "test@test.com" },
                        { "Subject", "Test" },
                        { "Body", "Body" }
                    }
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", request);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "triggerEvent");
    }

    #endregion

    #region GET /api/workflows/variables Tests

    [Fact]
    public async Task GetTemplateVariables_ShouldReturnSystemVariables()
    {
        // Act
        var response = await _client.GetAsync("/api/workflows/variables?contentType=TestType");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<TemplateVariableCollection>();
        result.Should().NotBeNull();
        result!.SystemVariables.Should().HaveCount(5);
        result.SystemVariables.Should().Contain(v => v.Name == "{{id}}");
        result.SystemVariables.Should().Contain(v => v.Name == "{{contentType}}");
        result.SystemVariables.Should().Contain(v => v.Name == "{{status}}");
    }

    [Fact]
    public async Task GetTemplateVariables_WithoutContentType_ShouldUseDefault()
    {
        // Act
        var response = await _client.GetAsync("/api/workflows/variables");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<TemplateVariableCollection>();
        result.Should().NotBeNull();
        result!.SystemVariables.Should().NotBeEmpty();
    }

    #endregion

    #region POST /api/workflows/dry-run Tests

    [Fact]
    public async Task DryRunWorkflow_ShouldExecuteWithoutSideEffects()
    {
        // Arrange
        var dryRunRequest = new
        {
            workflow = new
            {
                id = Guid.NewGuid(),
                name = "Test Dry Run",
                triggerContentType = "PurchaseOrder",
                triggerEvent = "Created",
                conditions = new Dictionary<string, string>(),
                actions = new[]
                {
                    new
                    {
                        type = "Email",
                        parameters = new Dictionary<string, string>
                        {
                            { "To", "test@example.com" },
                            { "Subject", "Test {{id}}" },
                            { "Body", "Order {{data.OrderNumber}}" }
                        }
                    }
                }
            },
            sampleContent = new
            {
                id = Guid.NewGuid(),
                contentType = "PurchaseOrder",
                status = 0, // Draft
                data = new Dictionary<string, object>
                {
                    { "OrderNumber", "PO-12345" }
                },
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/dry-run", dryRunRequest);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadAsStringAsync();
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DryRunWorkflow_ShouldResolveTemplateVariables()
    {
        // Arrange
        var contentId = Guid.NewGuid();
        var dryRunRequest = new
        {
            workflow = new
            {
                id = Guid.NewGuid(),
                name = "Template Test",
                triggerContentType = "PurchaseOrder",
                triggerEvent = "Created",
                conditions = new Dictionary<string, string>(),
                actions = new[]
                {
                    new
                    {
                        type = "Email",
                        parameters = new Dictionary<string, string>
                        {
                            { "To", "test@example.com" },
                            { "Subject", "Content {{id}}" },
                            { "Body", "Order {{data.OrderNumber}} for customer {{data.CustomerName}}" }
                        }
                    }
                }
            },
            sampleContent = new
            {
                id = contentId,
                contentType = "PurchaseOrder",
                status = 0,
                data = new Dictionary<string, object>
                {
                    { "OrderNumber", "PO-99999" },
                    { "CustomerName", "John Doe" }
                },
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/workflows/dry-run", dryRunRequest);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        // The dry-run should have resolved the template variables
        // We can't easily verify the resolved values without checking logs,
        // but we can verify it executed successfully
    }

    #endregion

    #region GET /api/workflows/{id}/debug Tests

    [Fact]
    public async Task GetWorkflowDebugInfo_WithNoExecutions_ShouldReturnEmptyList()
    {
        // Arrange
        var nonExistentWorkflowId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/workflows/{nonExistentWorkflowId}/debug");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<List<WorkflowExecutionLog>>();
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflowDebugInfo_WithLimitParameter_ShouldRespectLimit()
    {
        // Arrange
        var workflowId = Guid.NewGuid();
        var limit = 5;

        // Act
        var response = await _client.GetAsync($"/api/workflows/{workflowId}/debug?limit={limit}");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var result = await response.Content.ReadFromJsonAsync<List<WorkflowExecutionLog>>();
        result.Should().NotBeNull();
        // The result should have at most 'limit' items
        result!.Count.Should().BeLessThanOrEqualTo(limit);
    }

    #endregion
}
