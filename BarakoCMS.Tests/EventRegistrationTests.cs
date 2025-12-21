using FastEndpoints;
using FluentAssertions;
using Xunit;
using barakoCMS.Models;
using barakoCMS.Features.Content.Create;
using barakoCMS.Features.Content.Update;
using barakoCMS.Features.Content.Get;
using barakoCMS.Features.Workflows;
using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class EventRegistrationTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public EventRegistrationTests(IntegrationTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _output.WriteLine($"[TEST] Connection String: {_fixture.ConnectionString}");
        _client = fixture.CreateClient();
    }

    private async Task<(string token, Guid userId)> CreateAdminUserAsync()
    {
        return await TestHelpers.CreateAdminUserAsync(_fixture);
    }

    [Fact]
    public async Task EventRegistration_EndToEnd_Scenario()
    {
        // --------------------------------------------------------------------------------
        // STEP 1: Create Admin User and Token
        // --------------------------------------------------------------------------------
        var (token, userId) = await CreateAdminUserAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // --------------------------------------------------------------------------------
        // STEP 2: Create Workflow
        // --------------------------------------------------------------------------------
        var workflow = new WorkflowDefinition
        {
            Name = "Send Email on Registration",
            TriggerContentType = "Registration",
            TriggerEvent = "Created",
            Conditions = new Dictionary<string, string>(), // Always trigger
            Actions = new List<WorkflowAction>
            {
                new WorkflowAction { Type = "Email", Parameters = new Dictionary<string, string> { { "To", "organizer@example.com" } } }
            }
        };

        var createWorkflowResponse = await _client.PostAsJsonAsync("/api/workflows", workflow);
        createWorkflowResponse.EnsureSuccessStatusCode();
        _output.WriteLine("[TEST] Workflow Created");

        // --------------------------------------------------------------------------------
        // STEP 3: Create an Event (Happy Path)
        // --------------------------------------------------------------------------------
        var eventData = new Dictionary<string, object>
        {
            { "Title", "Tech Conference 2025" },
            { "Date", "2025-12-01" },
            { "Capacity", 100 } // Note: JSON serialization of int might be tricky with Dictionary<string, object>
        };
        // Fix for System.Text.Json serialization of object
        // We might need to ensure numbers are handled correctly if the backend expects specific types.
        // But Marten handles dynamic data well.

        var createEventReq = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "Event",
            Data = eventData,
            Status = ContentStatus.Published
        };

        var createEventResp = await _client.PostAsJsonAsync("/api/contents", createEventReq);
        createEventResp.EnsureSuccessStatusCode();
        _output.WriteLine("[TEST] Event Created");
        var createEventResult = await createEventResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>();
        createEventResult.Should().NotBeNull();
        var eventId = createEventResult!.Id;

        // --------------------------------------------------------------------------------
        // STEP 4: Attendee Registers (Happy Path + Workflow Trigger)
        // --------------------------------------------------------------------------------
        var registrationData = new Dictionary<string, object>
        {
            { "EventId", eventId },
            { "AttendeeName", "John Doe" },
            { "Email", "john@example.com" }
        };

        var registerReq = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "Registration",
            Data = registrationData,
            Status = ContentStatus.Published
        };

        var regResp = await _client.PostAsJsonAsync("/api/contents", registerReq);
        regResp.EnsureSuccessStatusCode();
        _output.WriteLine("[TEST] Registration Created");

        // --------------------------------------------------------------------------------
        // STEP 5: Idempotency Check
        // --------------------------------------------------------------------------------
        var idempotencyKey = Guid.NewGuid().ToString();
        _client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var idempResp1 = await _client.PostAsJsonAsync("/api/contents", registerReq);
        idempResp1.EnsureSuccessStatusCode();
        _output.WriteLine("[TEST] Idempotency 1 Success");

        // Second request with same idempotency key should be rejected with 409 Conflict
        var idempResp2 = await _client.PostAsJsonAsync("/api/contents", registerReq);
        idempResp2.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _output.WriteLine("[TEST] Idempotency 2 Correctly Rejected (409 Conflict)");

        _client.DefaultRequestHeaders.Remove("Idempotency-Key");

        // --------------------------------------------------------------------------------
        // STEP 6: Sensitivity Check
        // --------------------------------------------------------------------------------
        var secretData = new Dictionary<string, object> { { "SecretCode", "12345" } };
        var createSecretReq = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "SecretDoc",
            Data = secretData,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Sensitive
        };

        var secretResp = await _client.PostAsJsonAsync("/api/contents", createSecretReq);
        secretResp.EnsureSuccessStatusCode();

        // --------------------------------------------------------------------------------
        // STEP 7: Rollback
        // --------------------------------------------------------------------------------
        var updateReq = new barakoCMS.Features.Content.Update.Request
        {
            Id = eventId,
            Data = new Dictionary<string, object> { { "Title", "Tech Conf 2025 - UPDATED" } }
        };
        var updateResp = await _client.PutAsJsonAsync($"/api/contents/{eventId}", updateReq);
        updateResp.EnsureSuccessStatusCode();

        var getResp = await _client.GetAsync($"/api/contents/{eventId}");
        var getResult = await getResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Get.Response>();

        // JSON deserialization of Dictionary<string, object> results in JsonElement for values
        var titleElement = (System.Text.Json.JsonElement)getResult!.Data["Title"];
        titleElement.GetString().Should().Be("Tech Conf 2025 - UPDATED");
    }
}
