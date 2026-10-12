using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Diagnostics;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Diagnostics;

/// <summary>
/// GET /api/incidents (#893): one list across the failure sources, grouped by fingerprint and
/// redacted on the server. One test per source, a redaction test whose markers are only absent
/// because the redactor removed them, and the gate with its positive control first.
/// </summary>
[Collection("Sequential")]
public class IncidentsEndpointTests(IntegrationTestFixture factory)
{
    private async Task<HttpClient> ClientAsync(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await factory.StoredUserTokenAsync(roles));
        return client;
    }

    private static async Task<List<JsonElement>> GroupsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static List<JsonElement> Titled(List<JsonElement> groups, string marker) =>
        groups.Where(g => g.GetProperty("title").GetString()!.Contains(marker, StringComparison.Ordinal)).ToList();

    private async Task StoreAsync(Action<IDocumentSession> store)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        store(session);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Client_errors_reported_twice_are_one_group_with_both_occurrences_counted()
    {
        var marker = $"incident-client-{Guid.NewGuid():N}"[..28];
        var anonymous = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            var post = await anonymous.PostAsJsonAsync("/api/client-errors", new
            {
                items = new[] { new { kind = "error", message = marker, source = "app.js", appVersion = "1.2.3" } },
            });
            post.IsSuccessStatusCode.Should().BeTrue();
        }

        var groups = await GroupsAsync(await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=client-errors&pageSize=100"));

        var mine = Titled(groups, marker);
        mine.Should().HaveCount(1, "two reports of the same fault are one incident");
        mine[0].GetProperty("source").GetString().Should().Be("client-errors");
        mine[0].GetProperty("count").GetInt32().Should().Be(2);
        mine[0].GetProperty("releases").EnumerateArray().Select(r => r.GetString()).Should().Equal("1.2.3");
    }

    [Fact]
    public async Task A_dead_lettered_job_is_an_incident()
    {
        var marker = $"DeadJob{Guid.NewGuid():N}"[..20];
        await StoreAsync(s => s.Store(new JobRecord
        {
            TrackingID = Guid.NewGuid(),
            QueueID = "q",
            CommandType = $"Probe.{marker}, Probe",
            CommandJson = "{}",
            CreatedAt = DateTime.UtcNow,
            ExecuteAfter = DateTime.UtcNow,
            ExpireOn = DateTime.UtcNow.AddDays(1),
            State = JobState.DeadLettered,
            AttemptCount = 5,
            CompletedAt = DateTime.UtcNow,
            LastError = "System.TimeoutException: the upstream did not answer",
        }));

        var groups = await GroupsAsync(await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=dead-letters&pageSize=100"));

        var mine = Titled(groups, marker);
        mine.Should().HaveCount(1);
        mine[0].GetProperty("title").GetString().Should().Be($"{marker} gave up after 5 attempts");
        mine[0].GetProperty("detail").GetString().Should().StartWith("System.TimeoutException");
    }

    [Fact]
    public async Task A_failed_workflow_action_is_an_incident()
    {
        var marker = $"wf-{Guid.NewGuid():N}"[..16];
        await StoreAsync(s => s.Store(new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = marker,
            ContentType = "post",
            TriggerEvent = "Published",
            Status = RunStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = "Webhook",
                    Status = AttemptStatus.Failed,
                    Attempts = 3,
                    Error = "HttpRequestException: connection refused",
                    CompletedAt = DateTimeOffset.UtcNow,
                },
            ],
        }));

        var groups = await GroupsAsync(await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=failed-runs&pageSize=100"));

        var mine = Titled(groups, marker);
        mine.Should().HaveCount(1);
        mine[0].GetProperty("title").GetString().Should().Be($"{marker}: Webhook failed");
    }

    [Fact]
    public async Task A_failed_delivery_is_an_incident_and_its_response_body_is_not_read()
    {
        var host = $"hook-{Guid.NewGuid():N}"[..20] + ".example.com";
        var body = $"response-body-{Guid.NewGuid():N}";
        await StoreAsync(s => s.Store(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Url = $"https://{host}",
            Event = "Published",
            ResponseStatus = 503,
            ResponseBody = body,
            CreatedAt = DateTimeOffset.UtcNow,
        }));

        var response = await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=failed-deliveries&pageSize=100");
        var raw = await response.Content.ReadAsStringAsync();
        var groups = await GroupsAsync(response);

        var mine = Titled(groups, host);
        mine.Should().HaveCount(1);
        mine[0].GetProperty("title").GetString().Should().Be($"Webhook to https://{host} answered 503");
        raw.Should().NotContain(body, "a response body can echo a credential and needs its own capability to read");
    }

    [Fact]
    public async Task Tokens_and_Sensitive_field_values_in_a_recorded_error_do_not_appear_in_the_response()
    {
        var marker = $"incident-redact-{Guid.NewGuid():N}"[..28];
        var field = $"Phone{Guid.NewGuid():N}"[..13];
        var phone = $"+63-917-{Random.Shared.Next(1000000, 9999999)}";
        var queryToken = $"q{Guid.NewGuid():N}";
        var bearer = $"b{Guid.NewGuid():N}";

        await StoreAsync(s => s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"patient{Guid.NewGuid():N}"[..15],
            DisplayName = "Patient",
            Fields = [new FieldDefinition { Name = field, DisplayName = "Phone", Type = "string", Sensitivity = SensitivityLevel.Sensitive }],
        }));

        var anonymous = factory.CreateClient();
        var post = await anonymous.PostAsJsonAsync("/api/client-errors", new
        {
            items = new[]
            {
                new
                {
                    kind = "api",
                    message = $"{marker} save failed for {{\"{field}\": \"{phone}\"}} Authorization: Bearer {bearer}",
                    source = $"https://app.example.com/reset?token={queryToken}",
                },
            },
        });
        post.IsSuccessStatusCode.Should().BeTrue();

        var response = await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=client-errors&pageSize=100");
        var raw = await response.Content.ReadAsStringAsync();
        var mine = Titled(await GroupsAsync(response), marker);

        // The control: the incident is there, so the absences below are the redactor's doing.
        mine.Should().HaveCount(1);
        mine[0].GetProperty("detail").GetString().Should().Contain("https://app.example.com/reset?token=");
        raw.Should().NotContain(phone, "a Sensitive field's value is removed");
        raw.Should().NotContain(bearer, "a bearer token is removed");
        raw.Should().NotContain(queryToken, "a query string value is removed");
    }

    [Fact]
    public async Task The_view_needs_manage_client_errors_and_each_source_its_own_capability()
    {
        var marker = $"DeadGate{Guid.NewGuid():N}"[..20];
        await StoreAsync(s => s.Store(new JobRecord
        {
            TrackingID = Guid.NewGuid(),
            QueueID = "q",
            CommandType = $"Probe.{marker}, Probe",
            CommandJson = "{}",
            CreatedAt = DateTime.UtcNow,
            ExecuteAfter = DateTime.UtcNow,
            ExpireOn = DateTime.UtcNow.AddDays(1),
            State = JobState.DeadLettered,
            AttemptCount = 1,
            LastError = "boom",
        }));

        // Positive control: an Admin holds the view and view_jobs, and sees the job.
        var admin = await GroupsAsync(await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=dead-letters&pageSize=100"));
        Titled(admin, marker).Should().HaveCount(1);

        // A role with the view's capability only: the view answers, the dead-letter source is left out.
        var roleName = $"IncidentsOnly{Guid.NewGuid():N}"[..24];
        await StoreAsync(s => s.Store(new Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = [DiagnosticsCapabilities.ManageClientErrors],
        }));
        var limited = await GroupsAsync(await (await ClientAsync(roleName)).GetAsync("/api/incidents?source=dead-letters&pageSize=100"));
        Titled(limited, marker).Should().BeEmpty("dead letters are read with view_jobs, which this role does not hold");

        (await factory.CreateClient().GetAsync("/api/incidents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await ClientAsync("User")).GetAsync("/api/incidents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
