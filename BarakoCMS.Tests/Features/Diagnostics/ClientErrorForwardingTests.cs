using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Diagnostics.Forwarding;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests.Features.Diagnostics;

/// <summary>
/// The optional Sentry-compatible forwarder (#627): off unless configured, sends a redacted
/// envelope to a stub receiver, never lets the receiver touch the ingest response, and drops rather
/// than queues past its capacity.
/// </summary>
[Collection("Sequential")]
public class ClientErrorForwardingTests(IntegrationTestFixture factory)
{
    private const string Dsn = "https://publickey123@errors.example.test/42";

    /// <summary>A receiver that records what reaches it and answers however the test says.</summary>
    private sealed class StubReceiver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Received { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Enqueue((request, await request.Content!.ReadAsStringAsync(ct)));
            return await answer(request, ct);
        }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWith(
        StubReceiver receiver, bool enabled = true, int timeoutSeconds = 1) =>
        factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Diagnostics:Forwarding:Enabled"] = enabled ? "true" : "false",
                ["Modules:Diagnostics:Forwarding:Dsn"] = Dsn,
                ["Modules:Diagnostics:Forwarding:Environment"] = "test",
                ["Modules:Diagnostics:Forwarding:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            b.ConfigureTestServices(services =>
                services.AddHttpClient<SentryForwardClient>().ConfigurePrimaryHttpMessageHandler(() => receiver));
        });

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, string message) =>
        client.PostAsJsonAsync("/api/client-errors", new
        {
            items = new[] { new { kind = "error", message, source = "app.js", url = "https://app.example.com/page?session=abc123secret", appVersion = "9.9.9" } },
        });

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task A_reported_error_reaches_the_receiver_as_a_redacted_envelope()
    {
        var receiver = new StubReceiver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var resolver = FastEndpointsResolver.Keep();
        await using var host = HostWith(receiver);
        var marker = $"forward-{Guid.NewGuid():N}"[..20];
        var bearer = $"b{Guid.NewGuid():N}";

        (await ReportAsync(host.CreateClient(), $"{marker} failed with Bearer {bearer}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await EventuallyAsync(() => receiver.Received.Any(r => r.Body.Contains(marker)))).Should().BeTrue("the report is forwarded");
        var (request, body) = receiver.Received.First(r => r.Body.Contains(marker));
        request.RequestUri.Should().Be(new Uri("https://errors.example.test/api/42/envelope/"));
        request.Headers.GetValues("X-Sentry-Auth").Single().Should().Contain("sentry_key=publickey123");
        body.Should().Contain("\"release\":\"9.9.9\"").And.Contain("\"environment\":\"test\"");
        body.Should().NotContain(bearer, "a bearer token never leaves the process");
        body.Should().NotContain("abc123secret", "a query string value never leaves the process");
    }

    [Fact]
    public async Task Ingest_answers_while_the_receiver_is_still_holding_the_send()
    {
        // The receiver does not answer until the test lets it, and the send timeout is longer than
        // the test, so an ingest that waited on the send could not answer at all.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new StubReceiver(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var resolver = FastEndpointsResolver.Keep();
        await using var host = HostWith(receiver, timeoutSeconds: 60);
        var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            (await ReportAsync(client, $"forward-held-{Guid.NewGuid():N}")).StatusCode.Should().Be(HttpStatusCode.OK);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.Task.IsCompleted.Should().BeFalse("the response came back while the receiver still held the send");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("down")]
    [InlineData("wrong")]
    public async Task A_receiver_that_is_down_or_wrong_does_not_affect_the_ingest_response(string failure)
    {
        var receiver = new StubReceiver((_, _) => failure == "down"
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var resolver = FastEndpointsResolver.Keep();
        await using var host = HostWith(receiver);
        var client = host.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await ReportAsync(client, $"forward-{failure}-{i}-{Guid.NewGuid():N}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await EventuallyAsync(() => receiver.Received.Count >= 3)).Should().BeTrue(
            "the control: every report reached the receiver, and the sender kept going after each failure");
    }

    [Fact]
    public async Task Forwarding_is_off_unless_configured()
    {
        var receiver = new StubReceiver((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var resolver = FastEndpointsResolver.Keep();
        await using var host = HostWith(receiver, enabled: false);

        host.Services.GetRequiredService<ClientErrorForwarder>().Enabled.Should().BeFalse();
        (await ReportAsync(host.CreateClient(), $"forward-off-{Guid.NewGuid():N}")).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(500);
        receiver.Received.Should().BeEmpty();
    }

    [Fact]
    public void A_full_queue_drops_new_reports_instead_of_growing()
    {
        var forwarder = new ClientErrorForwarder(Options.Create(new ForwardingOptions { Enabled = true, Dsn = Dsn, QueueCapacity = 2 }));
        ForwardedError Error(int n) => new($"f{n}", "error", "error", $"m{n}", null, null, null, null, null, DateTimeOffset.UtcNow);

        forwarder.Enabled.Should().BeTrue();
        forwarder.TryEnqueue(Error(1)).Should().BeTrue();
        forwarder.TryEnqueue(Error(2)).Should().BeTrue();
        forwarder.TryEnqueue(Error(3)).Should().BeFalse("the queue holds two and nothing reads it here");
        forwarder.Dropped.Should().Be(1);
    }

    [Theory]
    [InlineData("https://key@o1.ingest.example.com/7", "https://o1.ingest.example.com/api/7/envelope/", "key")]
    [InlineData("https://key:secret@glitchtip.example.com/sub/12", "https://glitchtip.example.com/sub/api/12/envelope/", "key")]
    public void A_dsn_gives_the_envelope_endpoint_and_public_key(string dsn, string endpoint, string key)
    {
        var parsed = SentryDsn.TryParse(dsn);
        parsed.Should().NotBeNull();
        parsed!.EnvelopeEndpoint.Should().Be(new Uri(endpoint));
        parsed.PublicKey.Should().Be(key);
        parsed.Public.Should().NotContain("secret", "the envelope header never carries a secret key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://errors.example.com/7")]
    [InlineData("ftp://key@errors.example.com/7")]
    public void A_dsn_that_does_not_parse_leaves_forwarding_off(string? dsn)
    {
        new ClientErrorForwarder(Options.Create(new ForwardingOptions { Enabled = true, Dsn = dsn })).Enabled.Should().BeFalse();
    }
}
