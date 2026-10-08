using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using barakoCMS.Core.Interfaces;
using barakoCMS.Extensions;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The retry, timeout and per-host breaker inside one workflow attempt (#703), driven through a
/// scripted inner handler so no test touches the network.
/// </summary>
/// <remarks>
/// Every host is a fresh name, because Carom keeps breakers for the whole process and a host shared
/// between two tests would carry one test's failures into the other.
/// </remarks>
public class OutboundResilienceTests
{
    private static readonly OutboundAddressGuard AllowingGuard = new(
        resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
        isBlocked: _ => false);

    [Fact]
    public async Task Two_503s_then_a_200_is_one_success_after_three_calls()
    {
        var stub = new ScriptedHandler(
            Answer(HttpStatusCode.ServiceUnavailable),
            Answer(HttpStatusCode.ServiceUnavailable),
            Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast());

        using var response = await client.PostAsync(Url(), new StringContent("payload"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(3);
        stub.Bodies.Should().HaveCount(3);
        stub.Bodies.Should().AllBe("payload", "a resent body that arrives empty is answered without complaint");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_status_that_is_not_transient_is_sent_once(HttpStatusCode status)
    {
        var stub = new ScriptedHandler(Answer(status));
        using var client = Client(stub, Fast());

        using var response = await client.PostAsync(Url(), new StringContent("payload"), Ct);

        response.StatusCode.Should().Be(status);
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_last_transient_answer_is_handed_back_when_the_tries_are_spent()
    {
        var stub = new ScriptedHandler(Answer(HttpStatusCode.ServiceUnavailable));
        using var client = Client(stub, Fast());

        using var response = await client.GetAsync(Url(), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the caller records the status, so the 503 still has to reach it");
        stub.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_Retry_After_within_the_budget_is_waited_for()
    {
        var stub = new ScriptedHandler(
            Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(1)),
            Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast());

        var timer = Stopwatch.StartNew();
        using var response = await client.PostAsync(Url(), new StringContent("payload"), Ct);
        timer.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(2);
        timer.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950),
            "the jitter alone is at most 5 ms here, so only the Retry-After explains a second's wait");
    }

    [Fact]
    public async Task A_Retry_After_past_the_budget_hands_back_the_429_after_one_call()
    {
        var stub = new ScriptedHandler(
            Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(5)),
            Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast());

        var timer = Stopwatch.StartNew();
        using var response = await client.PostAsync(Url(), new StringContent("payload"), Ct);
        timer.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        stub.Calls.Should().Be(1);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a long wait belongs to the durable queue");
    }

    [Fact]
    public async Task The_breaker_opens_after_the_threshold_for_that_host_and_not_for_another()
    {
        var failing = $"{Guid.NewGuid():N}.example";
        var healthy = $"{Guid.NewGuid():N}.example";
        var stub = new ScriptedHandler((request, _) => Task.FromResult(Status(
            request.RequestUri!.Host == failing ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var client = Client(stub, Fast(retries: 0, breakerFailures: 3));

        for (var i = 0; i < 3; i++)
        {
            using var answer = await client.GetAsync($"https://{failing}/hooks/secret-path?token=abc", Ct);
            answer.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        stub.Calls.Should().Be(3);

        var paused = async () => await client.GetAsync($"https://{failing}/hooks/secret-path?token=abc", Ct);
        var refused = await paused.Should().ThrowAsync<OutboundCircuitOpenException>();
        refused.Which.Message.Should().Contain(failing);
        refused.Which.Message.Should().NotContain("secret-path").And.NotContain("token");
        stub.Calls.Should().Be(3, "an open breaker sends nothing to the failing host");

        using var other = await client.GetAsync($"https://{healthy}/", Ct);
        other.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(4, "another host has its own breaker");
    }

    [Fact]
    public async Task A_connection_that_could_not_be_opened_is_retried_even_for_a_POST()
    {
        var stub = new ScriptedHandler(
            Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused")),
            Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused")),
            Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast());

        using var response = await client.PostAsync(Url(), new StringContent("payload"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(3);
    }

    [Fact]
    public async Task An_address_the_guard_refused_is_not_retried()
    {
        var stub = new ScriptedHandler(Throw(new HttpRequestException(
            HttpRequestError.ConnectionError, "refused", new BlockedAddressException("blocked"))));
        using var client = Client(stub, Fast());

        var send = async () => await client.GetAsync(Url(), Ct);

        await send.Should().ThrowAsync<HttpRequestException>();
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_POST_that_timed_out_is_not_sent_again()
    {
        var stub = new ScriptedHandler(Hang, Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast() with { AttemptTimeout = TimeSpan.FromMilliseconds(200) });

        var send = async () => await client.PostAsync(Url(), new StringContent("payload"), Ct);

        await send.Should().ThrowAsync<TimeoutException>();
        stub.Calls.Should().Be(1, "the first POST may have reached the server, and nothing lets it recognise a second");
    }

    [Fact]
    public async Task A_POST_marked_replayable_that_timed_out_is_sent_again()
    {
        var stub = new ScriptedHandler(Hang, Answer(HttpStatusCode.OK));
        using var client = Client(stub, Fast() with { AttemptTimeout = TimeSpan.FromMilliseconds(200) });

        using var request = new HttpRequestMessage(HttpMethod.Post, Url()) { Content = new StringContent("payload") };
        request.Options.Set(OutboundResilienceHandler.Replayable, true);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_open_breaker_fails_the_webhook_as_retryable_and_names_only_the_host()
    {
        var host = $"{Guid.NewGuid():N}.example";
        var url = $"https://{host}/hooks/secret-path?token=abc";
        var stub = new ScriptedHandler(Answer(HttpStatusCode.ServiceUnavailable));
        var action = new WebhookAction(
            new StubHttpClientFactory(Client(stub, Fast(retries: 0, breakerFailures: 2))),
            new Moq.Mock<Marten.IDocumentSession>().Object,
            new Moq.Mock<barakoCMS.Infrastructure.Security.ISecretProtector>().Object,
            AllowingGuard,
            NullLogger<WebhookAction>.Instance);
        var content = new Content { Id = Guid.NewGuid(), ContentType = "post", Sensitivity = SensitivityLevel.Sensitive };

        for (var i = 0; i < 2; i++)
        {
            var failed = await action.RunAsync(new() { ["Url"] = url }, content, Ct);
            failed.Error.Should().Contain("503");
        }

        var paused = await action.RunAsync(new() { ["Url"] = url }, content, Ct);

        paused.Succeeded.Should().BeFalse();
        paused.Retryable.Should().BeTrue("the durable queue tries again after its backoff");
        paused.Error.Should().Contain(host).And.Contain("paused");
        paused.Error.Should().NotContain("secret-path").And.NotContain("token");
        stub.Calls.Should().Be(2);
    }

    [Fact]
    public async Task The_breaker_for_a_shared_host_is_per_tenant()
    {
        var host = $"{Guid.NewGuid():N}.example";
        var stub = new ScriptedHandler((request, _) => Task.FromResult(Status(
            TenantOf(request) == "a" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var client = Client(stub, Fast(retries: 0, breakerFailures: 2));

        for (var i = 0; i < 2; i++)
        {
            using var failed = await client.SendAsync(ForTenant("a", host), Ct);
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        var paused = async () => await client.SendAsync(ForTenant("a", host), Ct);
        await paused.Should().ThrowAsync<OutboundCircuitOpenException>("tenant a's breaker is open");

        using var other = await client.SendAsync(ForTenant("b", host), Ct);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "tenant a's failures are not tenant b's");
        stub.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_429_or_a_long_Retry_After_never_opens_the_breaker()
    {
        var host = $"{Guid.NewGuid():N}.example";
        var answers = new Queue<HttpResponseMessage>([
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromMinutes(5)),
            Status(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromMinutes(5)),
            Status(HttpStatusCode.OK),
        ]);
        var stub = new ScriptedHandler((_, _) => Task.FromResult(answers.Dequeue()));
        using var client = Client(stub, Fast(retries: 0, breakerFailures: 2));

        for (var i = 0; i < 5; i++)
        {
            using var answer = await client.SendAsync(ForTenant("a", host), Ct);
        }

        stub.Calls.Should().Be(5, "a quota answer is one account's limit, not the host failing, so every call went out");
        answers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_email_is_sent_once_and_left_to_the_durable_queue()
    {
        var provider = new ScriptedEmail(_ => throw new InvalidOperationException("SMTP send failed"), _ => Task.CompletedTask);
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance, resilience: new OutboundResilience(Fast()));

        var result = await action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        provider.Calls.Should().Be(1, "nothing tells a send that never left from one the relay may have taken");
    }

    [Fact]
    public async Task An_email_the_provider_did_not_take_is_tried_again_inside_the_attempt()
    {
        var provider = new ScriptedEmail(
            _ => throw new EmailNotSentException("Relay refused the connection."),
            _ => throw new EmailNotSentException("Relay refused the connection."),
            _ => Task.CompletedTask);
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance, resilience: new OutboundResilience(Fast()));

        var result = await action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        result.Succeeded.Should().BeTrue();
        provider.Calls.Should().Be(3, "two sends that never left, then the one that did");
    }

    [Fact]
    public async Task An_email_never_taken_is_tried_the_configured_times_then_left_to_the_durable_queue()
    {
        var provider = new ScriptedEmail(_ => throw new EmailNotSentException("Relay refused the connection."));
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance, resilience: new OutboundResilience(Fast(retries: 1)));

        var result = await action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_email_not_sent_and_then_failed_otherwise_is_not_sent_a_third_time()
    {
        var provider = new ScriptedEmail(
            _ => throw new EmailNotSentException("Relay refused the connection."),
            _ => throw new InvalidOperationException("The relay dropped the line after the message."),
            _ => Task.CompletedTask);
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance, resilience: new OutboundResilience(Fast()));

        var result = await action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        result.Succeeded.Should().BeFalse();
        provider.Calls.Should().Be(2, "the second failure may be a message the relay took");
    }

    [Fact]
    public async Task An_email_send_past_its_timeout_after_a_send_that_left_is_not_tried_again()
    {
        var provider = new ScriptedEmail(
            _ => throw new EmailNotSentException("Relay refused the connection."),
            token => Task.Delay(TimeSpan.FromSeconds(30), token),
            _ => Task.CompletedTask);
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance,
            resilience: new OutboundResilience(Fast() with { EmailSendTimeout = TimeSpan.FromMilliseconds(300) }));

        var send = () => action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        await send.Should().ThrowAsync<OperationCanceledException>().WithMessage("*not known whether the email was sent*");
        provider.Calls.Should().Be(2, "the timed out send may have gone out");
    }

    [Fact]
    public void An_email_send_has_no_timeout_by_default()
    {
        new OutboundResilienceOptions().EmailSendTimeout.Should().BeNull();
        OutboundResilienceOptions.FromConfiguration(new ConfigurationBuilder().Build()).EmailSendTimeout.Should().BeNull(
            "a large attachment over a slow relay used to send, and a default limit would stop it");
    }

    [Fact]
    public async Task An_email_send_past_its_timeout_is_unknown_rather_than_a_retryable_failure()
    {
        var provider = new ScriptedEmail(token => Task.Delay(TimeSpan.FromSeconds(30), token), _ => Task.CompletedTask);
        var action = new EmailAction(provider, NullLogger<EmailAction>.Instance,
            resilience: new OutboundResilience(Fast() with { EmailSendTimeout = TimeSpan.FromMilliseconds(200) }));

        var send = () => action.RunAsync(Email(), new Content { Id = Guid.NewGuid() }, Ct);

        // The runner records an OperationCanceledException that is not its own as Unknown and does
        // not retry it (WorkflowRunner, the catch after RunAsync).
        await send.Should().ThrowAsync<OperationCanceledException>().WithMessage("*not known whether the email was sent*");
        provider.Calls.Should().Be(1, "a send that timed out may have gone out, and a second one is a second email");
    }

    [Fact]
    public void The_slowest_action_with_the_defaults_fits_in_the_shortest_lease()
    {
        var defaults = new OutboundResilienceOptions();
        var lease = OutboundResilienceOptions.ShortestLease(JobOptions.DefaultLeaseSeconds);

        lease.Should().Be(WorkflowRetryPolicy.LeaseDuration, "the runner's 5 minutes is shorter than the job queue's 10");
        defaults.MaxHttpDuration.Should().Be(TimeSpan.FromSeconds(44));
        defaults.ClientTimeout.Should().Be(TimeSpan.FromSeconds(74));
        defaults.MaxActionDuration.Should().Be(TimeSpan.FromSeconds(208),
            "a connector request is two grants capped at 30 s and two sends capped by the client timeout");
        defaults.MaxActionDuration.Should().BeLessThanOrEqualTo(lease * OutboundResilienceOptions.LeaseShare);

        var act = () => defaults.Validate(JobOptions.DefaultLeaseSeconds);
        act.Should().NotThrow();
    }

    [Fact]
    public void An_email_timeout_longer_than_the_lease_is_refused()
    {
        var act = () => new OutboundResilienceOptions { EmailSendTimeout = TimeSpan.FromMinutes(5) }
            .Validate(JobOptions.DefaultLeaseSeconds);

        act.Should().Throw<InvalidOperationException>().WithMessage("*shortest lease*");
    }

    [Fact]
    public void Settings_that_could_outlast_the_lease_stop_the_host_at_startup()
    {
        var accepted = () => Register(new Dictionary<string, string?>());
        accepted.Should().NotThrow("the defaults fit, so the refusals below are about the values");

        var longTries = () => Register(new() { [OutboundResilienceOptions.AttemptTimeoutSecondsKey] = "60" });
        longTries.Should().Throw<InvalidOperationException>().WithMessage("*Workflows:Outbound*shortest lease*");

        var shortLease = () => Register(new() { [JobOptions.LeaseSecondsKey] = "60" });
        shortLease.Should().Throw<InvalidOperationException>().WithMessage("*Workflows:Outbound*shortest lease (60 s*");
    }

    private static void Register(Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none";
        settings["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        new ServiceCollection().AddBarakoCMS(configuration, m => m.Discover = false);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url() => $"https://{Guid.NewGuid():N}.example/hook";

    private static Dictionary<string, string> Email() => new()
    {
        ["To"] = "someone@example.com",
        ["Subject"] = "Subject",
        ["Body"] = "Body",
    };

    private static OutboundResilienceOptions Fast(int retries = 2, int breakerFailures = 0) => new()
    {
        Retries = retries,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
        AttemptTimeout = TimeSpan.FromSeconds(5),
        MaxRetryAfter = TimeSpan.FromSeconds(2),
        BreakerFailures = breakerFailures,
        BreakerWindow = Math.Max(breakerFailures, 1),
        BreakerSampling = TimeSpan.FromMinutes(5),
        BreakerOpen = TimeSpan.FromMinutes(5),
    };

    private static HttpClient Client(HttpMessageHandler inner, OutboundResilienceOptions options) =>
        new(new OutboundResilienceHandler(new OutboundResilience(options)) { InnerHandler = inner });

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private static HttpRequestMessage ForTenant(string tenant, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
        OutboundResilienceHandler.SetTenant(request, tenant);
        return request;
    }

    private static string? TenantOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(OutboundResilienceHandler.Tenant, out var tenant) ? tenant : null;

    private static Step Answer(HttpStatusCode status, TimeSpan? retryAfter = null) =>
        (_, _) => Task.FromResult(Status(status, retryAfter));

    private static Step Throw(Exception ex) => (_, _) => Task.FromException<HttpResponseMessage>(ex);

    /// <summary>Never answers, so only the try's own timeout ends it.</summary>
    private static readonly Step Hang = async (_, ct) =>
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        return Status(HttpStatusCode.OK);
    };

    private delegate Task<HttpResponseMessage> Step(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Answers each call with the next step, repeating the last one, and records each body it was sent.</summary>
    private sealed class ScriptedHandler(params Step[] steps) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = steps[Math.Min(Interlocked.Increment(ref _calls), steps.Length) - 1];
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));

            return await step(request, ct);
        }
    }

    private sealed class ScriptedEmail(params Func<CancellationToken, Task>[] steps) : IEmailService
    {
        public int Calls { get; private set; }

        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
            steps[Math.Min(++Calls, steps.Length) - 1](cancellationToken);
    }
}
