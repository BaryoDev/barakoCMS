using System.Net;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #559: a burst of publishes fires one static build, the post goes through the webhook path so
/// a failure shows in WebhookDeliveries, and each provider gets the body and headers it expects.
/// </summary>
[Collection("Integration1")]
public class BuildHookActionTests
{
    private const string HookUrl = "https://hook.example/build_hooks/abc123";

    private readonly IntegrationTestFixture _fixture;

    public BuildHookActionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store() => _fixture.Services.GetRequiredService<IDocumentStore>();

    private ISecretProtector Protector() => _fixture.Services.GetRequiredService<ISecretProtector>();

    private static string NewTenant() => "buildhook-" + Guid.NewGuid().ToString("N");

    private static Content Published() => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "article",
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
    };

    private static Dictionary<string, string> Netlify(Guid workflowId, int? quiet = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Preset"] = "netlify",
            ["Url"] = HookUrl,
            ["WorkflowId"] = workflowId.ToString(),
            ["TriggerEvent"] = "Published",
        };
        if (quiet is not null) parameters["QuietSeconds"] = quiet.Value.ToString();
        return parameters;
    }

    private static async Task<WorkflowActionResult> PublishAsync(
        IDocumentStore store, string tenant, ManualClock clock, Dictionary<string, string> parameters)
    {
        await using var session = store.LightweightSession(tenant);
        var action = new BuildHookAction(session, NullLogger<BuildHookAction>.Instance, clock);
        return await action.RunAsync(parameters, Published(), TestContext.Current.CancellationToken);
    }

    private BuildHookSweepService Sweeper(ManualClock clock, RecordingHandler handler) => new(
        Store(),
        new ConfigurationBuilder().Build(),
        new FixedClientFactory(handler),
        Protector(),
        new OutboundAddressGuard(
            resolve: (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.10") }),
            isBlocked: _ => false),
        NullLoggerFactory.Instance,
        clock);

    private async Task<int> SweepAsync(string tenant, BuildHookSweepService sweeper)
    {
        await using var session = Store().LightweightSession(tenant);
        return await sweeper.SweepTenantAsync(session, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Twelve_publishes_inside_the_quiet_period_post_the_hook_once()
    {
        var tenant = NewTenant();
        var clock = new ManualClock();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sweeper = Sweeper(clock, handler);
        var parameters = Netlify(Guid.NewGuid(), quiet: 60);

        var triggers = new List<WorkflowActionResult>();
        for (var i = 0; i < 12; i++)
        {
            triggers.Add(await PublishAsync(Store(), tenant, clock, parameters));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        triggers.Should().HaveCount(12);
        triggers.Should().OnlyContain(t => t.Succeeded);
        triggers.Count(t => t.Outputs["build"] == "scheduled").Should().Be(1);
        triggers.Count(t => t.Outputs["build"] == "debounced").Should().Be(11);

        (await SweepAsync(tenant, sweeper)).Should().Be(0, "the quiet period has not ended");
        handler.Requests.Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(60));
        (await SweepAsync(tenant, sweeper)).Should().Be(1);

        handler.Requests.Should().HaveCount(1);
        (await SweepAsync(tenant, sweeper)).Should().Be(0, "the row is gone once it has fired");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_publish_after_the_window_fires_a_second_build()
    {
        var tenant = NewTenant();
        var clock = new ManualClock();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sweeper = Sweeper(clock, handler);
        var parameters = Netlify(Guid.NewGuid(), quiet: 30);

        await PublishAsync(Store(), tenant, clock, parameters);
        clock.Advance(TimeSpan.FromSeconds(31));
        await SweepAsync(tenant, sweeper);
        handler.Requests.Should().HaveCount(1);

        var second = await PublishAsync(Store(), tenant, clock, parameters);
        second.Outputs["build"].Should().Be("scheduled", "the first period is over");
        clock.Advance(TimeSpan.FromSeconds(31));
        await SweepAsync(tenant, sweeper);

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_instances_sweeping_the_same_due_row_fire_it_once()
    {
        var tenant = NewTenant();
        var clock = new ManualClock();
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var parameters = Netlify(Guid.NewGuid(), quiet: 10);

        await PublishAsync(Store(), tenant, clock, parameters);
        clock.Advance(TimeSpan.FromSeconds(11));

        var results = await Task.WhenAll(
            SweepAsync(tenant, Sweeper(clock, handler)),
            SweepAsync(tenant, Sweeper(clock, handler)));

        results.Sum().Should().Be(1);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_500_from_the_hook_is_recorded_in_the_webhook_deliveries()
    {
        var tenant = NewTenant();
        var clock = new ManualClock();
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var workflowId = Guid.NewGuid();

        await PublishAsync(Store(), tenant, clock, Netlify(workflowId, quiet: 5));
        clock.Advance(TimeSpan.FromSeconds(6));
        await SweepAsync(tenant, Sweeper(clock, handler));

        await using var session = Store().QuerySession(tenant);
        var rows = await session.Query<WebhookDelivery>()
            .Where(d => d.WorkflowId == workflowId)
            .ToListAsync(TestContext.Current.CancellationToken);

        rows.Should().HaveCount(1);
        rows[0].ResponseStatus.Should().Be(500);
        rows[0].Url.Should().Be("https://hook.example", "the token in the path is not written down");
        rows[0].Event.Should().Be("Published");
    }

    [Theory]
    [InlineData("netlify", "trigger_title")]
    [InlineData("vercel", null)]
    [InlineData("cloudflare-pages", null)]
    public void A_url_preset_posts_a_json_body_and_no_credentials(string preset, string? expectedProperty)
    {
        var parameters = new Dictionary<string, string> { ["Preset"] = preset, ["Url"] = HookUrl };

        BuildHookPresets.Problem(parameters).Should().BeNull();
        var composed = BuildHookPresets.Compose(parameters, "default", bearer: null);

        using var body = JsonDocument.Parse(composed.Body);
        body.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        if (expectedProperty is not null) body.RootElement.TryGetProperty(expectedProperty, out _).Should().BeTrue();
        composed.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task The_github_preset_sends_a_repository_dispatch_with_a_bearer_token_that_is_not_recorded()
    {
        var tenant = NewTenant();
        var clock = new ManualClock();
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var workflowId = Guid.NewGuid();
        const string token = "ghp_buildhooktoken1234567890";
        var parameters = new Dictionary<string, string>
        {
            ["Preset"] = "github-dispatch",
            ["Repository"] = "acme/site",
            ["Secret"] = Protector().Protect(token),
            ["WorkflowId"] = workflowId.ToString(),
            ["QuietSeconds"] = "5",
        };

        await PublishAsync(Store(), tenant, clock, parameters);
        clock.Advance(TimeSpan.FromSeconds(6));
        await SweepAsync(tenant, Sweeper(clock, handler));

        handler.Requests.Should().HaveCount(1);
        var sent = handler.Requests[0];
        sent.Uri.Should().Be("https://api.github.com/repos/acme/site/dispatches");
        sent.Headers["Authorization"].Should().Be("Bearer " + token);
        sent.Headers.Should().ContainKey("User-Agent");
        sent.Headers.Should().NotContainKey("X-Barako-Signature", "the secret is a token here, not a signing key");
        using var body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("event_type").GetString().Should().Be("barako-build");
        body.RootElement.GetProperty("client_payload").GetProperty("tenant").GetString().Should().Be(tenant);

        await using var session = Store().QuerySession(tenant);
        var row = (await session.Query<WebhookDelivery>()
            .Where(d => d.WorkflowId == workflowId)
            .ToListAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Subject;
        row.ResponseStatus.Should().Be(204);
        row.RequestHeaders.Keys.Should().NotContain("Authorization");
        System.Text.Json.JsonSerializer.Serialize(row).Should().NotContain(token);
    }

    [Theory]
    [InlineData("nope", "https://hook.example/x", null)]
    [InlineData("netlify", null, null)]
    [InlineData("github-dispatch", null, "acme/site")]
    public async Task A_misconfigured_action_fails_permanently_and_schedules_nothing(string preset, string? url, string? repository)
    {
        var tenant = NewTenant();
        var parameters = new Dictionary<string, string> { ["Preset"] = preset, ["WorkflowId"] = Guid.NewGuid().ToString() };
        if (url is not null) parameters["Url"] = url;
        if (repository is not null) parameters["Repository"] = repository;

        var result = await PublishAsync(Store(), tenant, new ManualClock(), parameters);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        await using var session = Store().QuerySession(tenant);
        (await session.Query<PendingBuild>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    private sealed class ManualClock : TimeProvider
    {
        // Far ahead of real time. The host's own sweeper runs on the real clock every 15 seconds, and a
        // build due in the past is due to it too, so it would claim the build before the test sweeps.
        private DateTimeOffset _now = new(2100, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Sent(string Method, string Uri, Dictionary<string, string> Headers, string Body);

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private readonly List<Sent> _requests = [];

        public IReadOnlyList<Sent> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct));
            lock (_requests) _requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), headers, body));
            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
