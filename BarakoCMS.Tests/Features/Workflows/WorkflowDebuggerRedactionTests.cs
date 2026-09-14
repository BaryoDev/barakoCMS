using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #608: the execution log is served at <c>/api/workflows/{id}/debug</c>, so what it stores
/// is what anyone who can read workflow runs sees.
/// </summary>
[Collection("Sequential")]
public class WorkflowDebuggerRedactionTests : IAsyncLifetime
{
    private const string Recipient = "alice@example.test";
    private const string Credential = "Bearer sk-live-4f9c2a";

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public WorkflowDebuggerRedactionTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Dictionary<string, string> Parameters() => new()
    {
        // A credential under a name the sensitive-name rule does not recognise.
        ["Authorization"] = Credential,
        ["To"] = Recipient,
        ["Status"] = "Draft",
    };

    [Fact]
    public async Task A_stored_failure_keeps_the_exception_type_and_no_unlisted_parameter_value()
    {
        var session = new Mock<IDocumentSession>();
        WorkflowExecutionLog? stored = null;
        session.Setup(s => s.Store(It.IsAny<WorkflowExecutionLog[]>()))
            .Callback<WorkflowExecutionLog[]>(docs => stored = docs.Single());

        var debugger = new WorkflowDebugger(session.Object, new Mock<ILogger<WorkflowDebugger>>().Object);
        var log = debugger.StartExecution(Guid.NewGuid(), Guid.NewGuid());

        debugger.LogActionFailure(log, "Email", debugger.StartAction(log, "Email"),
            new InvalidOperationException($"SMTP refused {Recipient} with {Credential}"), Parameters());
        debugger.LogActionSuccess(log, "CreateTask", debugger.StartAction(log, "CreateTask"), Parameters());

        await debugger.CompleteExecutionAsync(log, Stopwatch.StartNew());

        stored.Should().NotBeNull();
        stored!.Actions.Should().HaveCount(2);
        stored.Actions[0].ErrorMessage.Should().Be(nameof(InvalidOperationException));
        stored.Actions.Should().AllSatisfy(a => a.ResolvedParameters.Should().Contain("Status", "Draft",
            "an allowlisted structural parameter is still useful to record"));

        var json = JsonSerializer.Serialize(stored);
        json.Should().NotContain(Recipient);
        json.Should().NotContain("sk-live-4f9c2a");
    }

    [Fact]
    public async Task A_log_stored_before_redaction_is_redacted_when_served()
    {
        var workflowId = Guid.NewGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new WorkflowExecutionLog
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflowId,
                ContentId = Guid.NewGuid(),
                ExecutedAt = DateTime.UtcNow,
                Success = false,
                Actions =
                [
                    new ActionExecutionLog
                    {
                        ActionType = "Email",
                        Success = false,
                        ErrorMessage = $"SMTP refused {Recipient} with {Credential}",
                        ResolvedParameters = Parameters(),
                    },
                ],
            });
            await session.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/workflows/{workflowId}/debug");
        response.IsSuccessStatusCode.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();

        var logs = JsonSerializer.Deserialize<List<WorkflowExecutionLog>>(body, JsonSerializerOptions.Web);
        logs.Should().HaveCount(1);
        logs![0].Actions.Should().HaveCount(1);
        logs[0].Actions[0].ResolvedParameters.Should().Contain("Status", "Draft");

        body.Should().NotContain(Recipient);
        body.Should().NotContain("sk-live-4f9c2a");
    }
}
