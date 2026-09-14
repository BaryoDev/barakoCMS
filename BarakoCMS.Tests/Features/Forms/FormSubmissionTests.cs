using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Forms;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// POST /api/public/forms/{slug}, and the endpoints that decide which content types are forms.
/// </summary>
/// <remarks>
/// Every test sends from its own client IP, because the submit limit is five per IP per ten minutes
/// and the suite would otherwise spend one shared bucket across every test here.
/// </remarks>
[Collection("Sequential")]
public class FormSubmissionTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;

    public FormSubmissionTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task An_accepted_submission_is_stored_sensitive_and_is_not_delivered()
    {
        var type = await CreateTypeAsync(publiclyDeliverable: true);
        await EnableAsync(type);
        var visitor = Visitor();

        var response = await visitor.PostAsJsonAsync($"/api/public/forms/{type}", new
        {
            data = new { name = "Ana", email = "ana@example.com", message = "Hello" },
            // Not part of the request. Bound to nothing, so a caller cannot choose any of them.
            status = "Published",
            sensitivity = "Public",
            createdBy = Guid.NewGuid(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        entries[0].Status.Should().Be(ContentStatus.Draft);
        entries[0].CreatedBy.Should().Be(Guid.Empty);
        entries[0].Data.Keys.Should().BeEquivalentTo(["name", "email", "message"]);

        // Draft alone would keep it out of delivery, which would make the next assertion pass for the
        // wrong reason. Publish it, and publish an ordinary entry beside it as the control.
        // SuperAdmin for the core content calls: the seeded Admin holds manage_forms but no
        // permission on a content type this test just made up.
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await admin.PutAsJsonAsync($"/api/contents/{entries[0].Id}/status",
            new { id = entries[0].Id, newStatus = ContentStatus.Published })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["name"] = "Control", ["email"] = "control@example.com" },
            status = ContentStatus.Published,
        })).EnsureSuccessStatusCode();

        var delivered = await _factory.CreateClient().GetAsync($"/api/public/{type}");
        delivered.StatusCode.Should().Be(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await delivered.Content.ReadAsStringAsync());
        var names = page.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("data").GetProperty("name").GetString())
            .ToList();

        names.Should().Equal(["Control"], "the published control is delivered and the published submission is not");
    }

    [Fact]
    public async Task A_submission_that_fails_validation_is_400_with_an_error_per_field()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);

        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", new
        {
            data = new { email = "not-an-email", message = "Hello" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorNamesAsync(response)).Should().BeEquivalentTo(["data.name", "data.email"]);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_and_non_public_fields_are_refused_alike()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);

        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", new
        {
            data = new { name = "Ana", email = "ana@example.com", internalNote = "vip", favouriteColour = "blue" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("reason").GetString()!);

        errors.Keys.Should().BeEquivalentTo(["data.internalNote", "data.favouriteColour"]);
        errors["data.internalNote"].Should().Be("'internalNote' is not a field this form accepts.",
            "a hidden field must be refused in words that do not reveal it exists");
        errors["data.favouriteColour"].Should().Be("'favouriteColour' is not a field this form accepts.");
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_filled_honeypot_is_answered_like_a_success_and_dropped()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);
        var payload = new { name = "Ana", email = "ana@example.com" };

        var real = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", new { data = payload });
        var bot = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", new { data = payload, honeypot = "http://spam.example" });

        real.StatusCode.Should().Be(HttpStatusCode.Accepted);
        bot.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await bot.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().HaveCount(1, "the real submission is stored and the bot's is not");
    }

    [Fact]
    public async Task Submissions_past_the_limit_for_one_client_are_429()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);
        var client = Visitor();

        for (var i = 0; i < 5; i++)
        {
            var allowed = await client.PostAsJsonAsync($"/api/public/forms/{type}", new { data = new { } });
            allowed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "within the limit the request reaches validation");
        }

        var refused = await client.PostAsJsonAsync($"/api/public/forms/{type}", new { data = new { } });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var other = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", email = "ana@example.com" } });
        other.StatusCode.Should().Be(HttpStatusCode.Accepted, "the limit is per client, not per form");
    }

    [Fact]
    public async Task A_type_that_is_not_a_form_is_404()
    {
        var type = await CreateTypeAsync(publiclyDeliverable: true);
        var submission = new { data = new { name = "Ana", email = "ana@example.com" } };

        (await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", submission)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Visitor().GetAsync($"/api/public/forms/{type}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Visitor().PostAsJsonAsync($"/api/public/forms/no-such-{Guid.NewGuid():N}", submission)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await EnableAsync(type);
        (await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", submission)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var admin = await AdminAsync();
        (await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = false })).EnsureSuccessStatusCode();
        (await Visitor().PostAsJsonAsync($"/api/public/forms/{type}", submission)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_definition_lists_only_the_fields_a_visitor_may_fill_in()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);

        var response = await Visitor().GetAsync($"/api/public/forms/{type}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToList();

        fields.Should().BeEquivalentTo(["name", "email", "message"],
            "internalNote is Sensitive and profile is json, and neither belongs on a public form");
    }

    [Fact]
    public async Task Marking_a_form_needs_the_capability_and_a_type_a_visitor_can_complete()
    {
        var type = await CreateTypeAsync();

        (await _factory.CreateClient().PutAsJsonAsync($"/api/forms/{type}", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = _factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("User"));
        (await user.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var blocked = await CreateTypeAsync(requiredSensitiveField: true);
        var admin = await AdminAsync();
        var refused = await admin.PutAsJsonAsync($"/api/forms/{blocked}", new { enabled = true });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorNamesAsync(refused)).Should().BeEquivalentTo(["fields.internalNote"]);

        (await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The submission fires the Created path the workflow engine listens to, proven end to end: a
    /// workflow on Created for the form's type records a finished run for the stored entry.
    /// </summary>
    [Fact]
    public async Task A_submission_fires_a_workflow_on_created()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type);
        var probeType = $"form-probe-{Guid.NewGuid():N}";
        var workflowId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new WorkflowDefinition
            {
                Id = workflowId,
                Name = $"notify-{Guid.NewGuid():N}",
                TriggerContentType = type,
                TriggerEvent = "Created",
                Actions =
                [
                    new WorkflowAction
                    {
                        Type = "CreateTask",
                        Parameters = new Dictionary<string, string> { ["ContentType"] = probeType, ["Title"] = "probe" },
                    },
                ],
            });
            await session.SaveChangesAsync();
        }

        (await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", email = "ana@example.com" } })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var entry = (await EntriesAsync(type)).Should().ContainSingle().Subject;

        WorkflowRun? run = null;
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            run = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<WorkflowRun>()
                .FirstOrDefaultAsync(r => r.WorkflowDefinitionId == workflowId);
            if (run is not null && run.Status is not (RunStatus.Pending or RunStatus.Running))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        run.Should().NotBeNull("a workflow on Created for the form's type never fired for the submission");
        run!.TriggerEvent.Should().Be("Created");
        run.ContentId.Should().Be(entry.Id);
        run.Status.Should().Be(RunStatus.Succeeded);
    }

    [Fact]
    public async Task With_turnstile_on_a_submission_needs_a_token_cloudflare_accepts()
    {
        var host = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Forms:Turnstile:Enabled"] = "true",
                ["Modules:Forms:Turnstile:SecretKey"] = "test-turnstile-secret",
            }));
            b.ConfigureServices(services => services
                .AddHttpClient<ITurnstileVerifier, TurnstileVerifier>()
                .ConfigurePrimaryHttpMessageHandler(() => new TurnstileStub()));
        });

        var type = await CreateTypeAsync();
        await EnableAsync(type);
        var data = new { name = "Ana", email = "ana@example.com" };

        var missing = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}", new { data });
        var rejected = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}", new { data, turnstileToken = "bad" });
        var passed = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}", new { data, turnstileToken = "good" });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorNamesAsync(missing)).Should().BeEquivalentTo(["turnstileToken"]);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        passed.StatusCode.Should().Be(HttpStatusCode.Accepted, await passed.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    /// <summary>Answers success only for the token "good" sent with the configured secret.</summary>
    private sealed class TurnstileStub : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            var ok = form.Contains("secret=test-turnstile-secret") && form.Contains("response=good");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"success\":{(ok ? "true" : "false")}}}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private HttpClient Visitor(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _factory).CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return client;
    }

    private async Task EnableAsync(string type)
    {
        var response = await (await AdminAsync()).PutAsJsonAsync($"/api/forms/{type}", new { enabled = true });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> CreateTypeAsync(bool publiclyDeliverable = false, bool requiredSensitiveField = false)
    {
        var name = $"form-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Contact request",
            IsPubliclyDeliverable = publiclyDeliverable,
            Fields =
            [
                new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                new FieldDefinition { Name = "email", DisplayName = "Email", Type = "email", IsRequired = true },
                new FieldDefinition { Name = "message", DisplayName = "Message", Type = "text" },
                new FieldDefinition
                {
                    Name = "internalNote", DisplayName = "Internal note", Type = "text",
                    Sensitivity = SensitivityLevel.Sensitive, IsRequired = requiredSensitiveField,
                },
                new FieldDefinition { Name = "profile", DisplayName = "Profile", Type = "json" },
            ],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<IReadOnlyList<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync();
    }

    private static async Task<List<string>> ErrorNamesAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
    }
}
