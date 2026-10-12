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
    private static string Letters(int n) =>
        new(Enumerable.Range(0, n).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private static int _ip;

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
        // Short and letters only, so the 32-character rule catches neither and each one is gone
        // only because its own rule removed it.
        var queryValue = "ref" + Letters(8);
        var bearer = "brr" + Letters(8);

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
                    message = $"{marker} save failed for {{\"{field}\": \"{phone}\"}} sent with Bearer {bearer}",
                    source = $"https://app.example.com/reset?ref={queryValue}",
                },
            },
        });
        post.IsSuccessStatusCode.Should().BeTrue();

        var response = await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=client-errors&pageSize=100");
        var raw = await response.Content.ReadAsStringAsync();
        var mine = Titled(await GroupsAsync(response), marker);

        // The control: the incident is there, so the absences below are the redactor's doing.
        mine.Should().HaveCount(1);
        mine[0].GetProperty("detail").GetString().Should().Contain("https://app.example.com/reset?ref=");
        raw.Should().NotContain(phone, "a Sensitive field's value is removed");
        raw.Should().NotContain(bearer, "a bearer token is removed");
        raw.Should().NotContain(queryValue, "a query string value is removed");
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
        var limitedClient = await ClientAsync(roleName);
        var limited = await GroupsAsync(await limitedClient.GetAsync("/api/incidents?source=dead-letters&pageSize=100"));
        Titled(limited, marker).Should().BeEmpty("dead letters are read with view_jobs, which this role does not hold");

        // The same for the two sources view_workflow_runs reads, each with an Admin control first.
        var run = $"wfgate-{Guid.NewGuid():N}"[..20];
        var host = $"gate-{Guid.NewGuid():N}"[..16] + ".example.com";
        await StoreAsync(s =>
        {
            s.Store(new WorkflowRun
            {
                Id = Guid.NewGuid(), WorkflowName = run, ContentType = "post", TriggerEvent = "Published",
                Status = RunStatus.Failed, CreatedAt = DateTimeOffset.UtcNow,
                Actions = [new WorkflowActionAttempt { ActionType = "Webhook", Status = AttemptStatus.Failed, Error = "boom" }],
            });
            s.Store(new WebhookDelivery
            {
                Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), Url = $"https://{host}", Event = "Published",
                ResponseStatus = 500, CreatedAt = DateTimeOffset.UtcNow,
            });
        });
        var adminClient = await ClientAsync("Admin");
        Titled(await GroupsAsync(await adminClient.GetAsync("/api/incidents?source=failed-runs&pageSize=100")), run).Should().HaveCount(1);
        Titled(await GroupsAsync(await adminClient.GetAsync("/api/incidents?source=failed-deliveries&pageSize=100")), host).Should().HaveCount(1);
        Titled(await GroupsAsync(await limitedClient.GetAsync("/api/incidents?source=failed-runs&pageSize=100")), run)
            .Should().BeEmpty("failed runs are read with view_workflow_runs");
        Titled(await GroupsAsync(await limitedClient.GetAsync("/api/incidents?source=failed-deliveries&pageSize=100")), host)
            .Should().BeEmpty("failed deliveries are read with view_workflow_runs");

        (await factory.CreateClient().GetAsync("/api/incidents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await ClientAsync("User")).GetAsync("/api/incidents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_dead_letter_is_dated_by_when_it_gave_up_and_its_url_is_cut_to_the_host()
    {
        var marker = $"LateJob{Guid.NewGuid():N}"[..20];
        var pathSecret = Letters(24);
        await StoreAsync(s => s.Store(new JobRecord
        {
            TrackingID = Guid.NewGuid(),
            QueueID = "q",
            CommandType = $"Probe.{marker}, Probe",
            CommandJson = "{}",
            CreatedAt = DateTime.UtcNow.AddDays(-40),
            ExecuteAfter = DateTime.UtcNow.AddDays(-40),
            ExpireOn = DateTime.UtcNow.AddDays(1),
            State = JobState.DeadLettered,
            AttemptCount = 3,
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            LastError = $"HttpRequestException: 403 from https://hooks.example.com/services/{pathSecret}",
        }));

        var response = await (await ClientAsync("Admin")).GetAsync("/api/incidents?source=dead-letters&days=30&pageSize=100");
        var raw = await response.Content.ReadAsStringAsync();
        var mine = Titled(await GroupsAsync(response), marker);

        mine.Should().HaveCount(1, "it was queued 40 days ago but gave up yesterday, inside the 30-day window");
        mine[0].GetProperty("detail").GetString().Should().Contain("https://hooks.example.com");
        raw.Should().NotContain(pathSecret, "a server-side error's URL is cut to its host, as /api/jobs cuts it");
    }

    [Fact]
    public async Task Groups_are_paged_with_the_total_counted_before_paging()
    {
        var marker = $"page-{Guid.NewGuid():N}"[..16];
        await StoreAsync(s =>
        {
            for (var i = 0; i < 3; i++)
            {
                s.Store(new ClientError
                {
                    Fingerprint = $"{marker}-{i}", Message = $"{marker} fault {i}", Tenant = "default",
                    FirstSeenAt = DateTime.UtcNow.AddYears(1).AddMinutes(i), LastSeenAt = DateTime.UtcNow.AddYears(1).AddMinutes(i),
                });
            }
        });

        // Seen in the future, so these three are the newest groups and fill the first page.
        var client = await ClientAsync("Admin");
        var first = await client.GetAsync("/api/incidents?source=client-errors&page=1&pageSize=2");
        var firstGroups = await GroupsAsync(first);
        using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        firstGroups.Should().HaveCount(2);
        firstGroups.Select(g => g.GetProperty("title").GetString()).Should().Equal($"{marker} fault 2", $"{marker} fault 1");
        doc.RootElement.GetProperty("totalItems").GetInt32().Should().BeGreaterThanOrEqualTo(3);

        var second = await GroupsAsync(await client.GetAsync("/api/incidents?source=client-errors&page=2&pageSize=2"));
        second.Should().NotBeEmpty();
        second[0].GetProperty("title").GetString().Should().Be($"{marker} fault 0");
    }

    [Fact]
    public async Task A_tenant_admin_sees_only_its_own_tenants_client_errors()
    {
        var tenantA = $"inc-{Guid.NewGuid():N}"[..16];
        var tenantB = $"inc-{Guid.NewGuid():N}"[..16];
        var marker = $"tenant-{Guid.NewGuid():N}"[..18];
        var adminId = Guid.NewGuid();
        await StoreAsync(s =>
        {
            s.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenantA, Name = tenantA, IsActive = true });
            s.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenantB, Name = tenantB, IsActive = true });
            s.Store(new User { Id = adminId, Username = $"u{adminId:N}", Email = $"u{adminId:N}@example.com" });
            s.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = adminId, TenantSlug = tenantA,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });
            s.Store(new ClientError { Fingerprint = $"{marker}-a", Message = $"{marker} in A", Tenant = tenantA });
            s.Store(new ClientError { Fingerprint = $"{marker}-b", Message = $"{marker} in B", Tenant = tenantB });
        });

        // The control: a global SuperAdmin sees both.
        var all = await GroupsAsync(await (await ClientAsync("SuperAdmin")).GetAsync("/api/incidents?source=client-errors&pageSize=100"));
        Titled(all, marker).Should().HaveCount(2);

        var tenantAdmin = factory.CreateClient();
        tenantAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.CreateToken(["Admin"], adminId.ToString(),
                new Dictionary<string, string> { ["tenant"] = tenantA, ["Username"] = $"u{adminId:N}" }));
        tenantAdmin.DefaultRequestHeaders.Add("X-Tenant", tenantA);
        tenantAdmin.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.101.{Interlocked.Increment(ref _ip) % 250 + 1}");

        var mine = Titled(await GroupsAsync(await tenantAdmin.GetAsync("/api/incidents?source=client-errors&pageSize=100")), marker);
        mine.Should().HaveCount(1);
        mine[0].GetProperty("tenants").EnumerateArray().Select(t => t.GetString()).Should().Equal(tenantA);
    }
}
