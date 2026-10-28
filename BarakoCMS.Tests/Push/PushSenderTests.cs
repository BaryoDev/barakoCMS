using BarakoCMS.Push;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Push;

[Collection("Sequential")]
public class PushSenderTests(IntegrationTestFixture fx)
{
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Retired_subscriptions_are_deleted(int status)
    {
        var h = await PushSenderHost.Create(fx, respond: _ => status);
        var userId = await h.Subscribe("https://push.example/gone");

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.Equal(1, result.Pruned);
        Assert.Equal(0, result.Delivered);
        Assert.Empty(await h.RowsFor(userId));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Other_failures_keep_the_subscription_and_count_as_failed(int status)
    {
        var h = await PushSenderHost.Create(fx, respond: _ => status);
        var userId = await h.Subscribe("https://push.example/keep");

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Pruned);
        Assert.Single(await h.RowsFor(userId));
    }

    [Fact]
    public async Task One_dead_device_does_not_stop_the_others()
    {
        var h = await PushSenderHost.Create(fx, respond: s => s.Endpoint.EndsWith("/dead") ? 410 : 201);
        var userId = await h.Subscribe("https://push.example/dead");
        await h.Subscribe("https://push.example/live", userId);

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.Equal(2, result.Attempted);
        Assert.Equal(1, result.Delivered);
        Assert.Equal(1, result.Pruned);
    }

    [Fact]
    public async Task A_transport_exception_counts_as_failed_and_does_not_throw()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => throw new HttpRequestException("boom"));
        var userId = await h.Subscribe("https://push.example/net");

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.Equal(1, result.Failed);
        Assert.Single(await h.RowsFor(userId));
    }

    [Fact]
    public async Task Send_only_reaches_the_named_users()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var a = await h.Subscribe("https://push.example/a");
        await h.Subscribe("https://push.example/b");

        await h.Sender.SendToUsersAsync(h.Tenant, [a], new("Hi", "There"), default);

        Assert.Single(h.Fake.Sent);
    }

    [Fact]
    public async Task Send_to_tenant_does_not_cross_tenants()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        await h.Subscribe("https://push.example/t1");
        var other = await PushSenderHost.Create(fx, respond: _ => 201);
        await other.Subscribe("https://push.example/t2");
        Assert.NotEqual(h.Tenant, other.Tenant);

        var result = await h.Sender.SendToTenantAsync(h.Tenant, new("Hi", "There"), default);

        Assert.Equal(1, result.Attempted);
    }

    [Fact]
    public async Task Unconfigured_sender_sends_nothing_and_says_disabled()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201, configured: false);
        var result = await h.Sender.SendToTenantAsync(h.Tenant, new("Hi", "There"), default);
        Assert.True(result.Disabled);
        Assert.Empty(h.Fake.Sent);
    }

    [Fact]
    public async Task A_failed_send_never_logs_the_endpoint_keys_or_private_key()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var userId = await h.Subscribe("https://push.example/secret-endpoint-token");
        var row = (await h.RowsFor(userId)).Single();
        h.Fake.Respond = _ => throw new HttpRequestException(
            $"failed {row.Endpoint} {row.P256dh} {row.Auth} {PushTestHost.PrivateKey}");
        CapturingLoggerProvider.Clear();

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "Private body"), default);

        Assert.Equal(1, result.Failed);
        var logs = string.Join("\n", CapturingLoggerProvider.Captured);
        Assert.Contains("1 failed", logs);
        Assert.DoesNotContain("secret-endpoint-token", logs);
        Assert.DoesNotContain(row.P256dh, logs);
        Assert.DoesNotContain(row.Auth, logs);
        Assert.DoesNotContain(PushTestHost.PrivateKey, logs);
        Assert.DoesNotContain("Private body", logs);
    }

    [Fact]
    public async Task A_stored_subscription_on_a_disallowed_host_is_skipped_not_sent()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var userId = await h.Subscribe("http://169.254.169.254/latest/meta-data");
        await h.Subscribe("https://evil.example/x", userId);
        await h.Subscribe("https://push.example/ok", userId);

        var result = await h.Sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.Equal(1, result.Delivered);
        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Pruned);
        var sent = Assert.Single(h.Fake.Sent);
        Assert.Equal((await h.RowsFor(userId)).Single(r => r.Endpoint.EndsWith("/ok")).Id, sent.SubscriptionId);
        Assert.Equal(3, (await h.RowsFor(userId)).Count);
    }

    [Fact]
    public async Task A_hanging_send_times_out_counts_as_failed_and_moves_on()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var userId = await h.Subscribe("https://push.example/hang");
        await h.Subscribe("https://push.example/fine", userId);
        var options = new PushOptions
        {
            PublicKey = PushTestHost.PublicKey,
            PrivateKey = PushTestHost.PrivateKey,
            Subject = "mailto:ops@example.org",
            AllowedHosts = ["push.example"],
            SendTimeout = TimeSpan.FromSeconds(1),
        };
        var sender = new PushSender(
            fx.Services.GetRequiredService<IDocumentStore>(), new HangingTransport(), options,
            NullLogger<PushSender>.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), default);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Delivered);
        Assert.Equal(2, (await h.RowsFor(userId)).Count);
    }

    [Fact]
    public async Task The_callers_token_still_cancels_the_whole_run()
    {
        var h = await PushSenderHost.Create(fx, respond: _ => 201);
        var userId = await h.Subscribe("https://push.example/hang");
        var options = new PushOptions
        {
            PublicKey = PushTestHost.PublicKey,
            PrivateKey = PushTestHost.PrivateKey,
            Subject = "mailto:ops@example.org",
            AllowedHosts = ["push.example"],
        };
        var sender = new PushSender(
            fx.Services.GetRequiredService<IDocumentStore>(), new HangingTransport(), options,
            NullLogger<PushSender>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendToUsersAsync(h.Tenant, [userId], new("Hi", "There"), cts.Token));
    }

    private sealed class HangingTransport : IPushTransport
    {
        public async Task<int> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken ct)
        {
            if (subscription.Endpoint.EndsWith("/hang")) await Task.Delay(Timeout.Infinite, ct);
            return 201;
        }
    }
}
