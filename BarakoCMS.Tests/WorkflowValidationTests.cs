using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class WorkflowValidationTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public WorkflowValidationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateWorkflow_RejectsInvalidTriggerEvent()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var invalid = new WorkflowDefinition
        {
            Name = "bad-trigger",
            TriggerContentType = "Article",
            TriggerEvent = "update", // not a valid event (valid: Created/Updated/Deleted/Published)
            Actions = new List<WorkflowAction>
            {
                new WorkflowAction
                {
                    Type = "Email",
                    Parameters = new Dictionary<string, string>
                    {
                        { "To", "a@b.com" }, { "Subject", "s" }, { "Body", "b" }
                    }
                }
            }
        };

        var resp = await _client.PostAsJsonAsync("/api/workflows", invalid);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateWorkflow_RejectsUnknownActionType()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var invalid = new WorkflowDefinition
        {
            Name = "bad-action",
            TriggerContentType = "Article",
            TriggerEvent = "Created",
            Actions = new List<WorkflowAction>
            {
                new WorkflowAction { Type = "Telepathy", Parameters = new Dictionary<string, string>() }
            }
        };

        var resp = await _client.PostAsJsonAsync("/api/workflows", invalid);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
