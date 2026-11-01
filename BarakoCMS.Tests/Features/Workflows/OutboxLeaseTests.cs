using barakoCMS.Features.Workflows.Outbox;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

[Collection("Integration1")]
public class OutboxLeaseTests
{
    private readonly IntegrationTestFixture _fixture;

    public OutboxLeaseTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store() => _fixture.Services.GetRequiredService<IDocumentStore>();

    private OutboxSender Sender(IOutboxTransport transport) => new(
        Store(), transport, new ConfigurationBuilder().Build(), NullLogger<OutboxSender>.Instance);

    private static string NewTenant() => "outbox-lease-" + Guid.NewGuid().ToString("N");

    private async Task<OutboxMessage> SeedAsync(string tenant, DateTimeOffset due)
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Tenant = tenant,
            ContentType = "article",
            ContentId = Guid.NewGuid(),
            Topic = "articles",
            MessageType = "published",
            Url = "https://receiver.example/in",
            Body = "{}",
            CreatedAt = due,
            NextAttemptAt = due,
        };

        await using var session = Store().LightweightSession(tenant);
        session.Store(message);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return message;
    }

    private async Task<OutboxMessage> LoadAsync(string tenant, Guid id)
    {
        await using var session = Store().QuerySession(tenant);
        return (await session.LoadAsync<OutboxMessage>(id, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task The_lease_is_stamped_from_the_clock_when_the_message_is_taken()
    {
        var tenant = NewTenant();
        var passStart = DateTimeOffset.UtcNow.AddHours(-1);
        var message = await SeedAsync(tenant, passStart.AddMinutes(-1));

        DateTimeOffset? leasedDuringSend = null;
        var transport = new FakeTransport(async (sent, ct) =>
        {
            leasedDuringSend = (await LoadAsync(tenant, sent.Id)).LeasedUntil;
            return OutboxSendResult.Ok(200, 1);
        });

        await using var session = Store().LightweightSession(tenant);
        var delivered = await Sender(transport).DeliverDueAsync(session, passStart, TestContext.Current.CancellationToken);

        delivered.Should().Be(1);
        leasedDuringSend.Should().NotBeNull();
        leasedDuringSend!.Value.Should().BeAfter(
            DateTimeOffset.UtcNow.AddMinutes(1),
            "a pass that started long ago must not hand out a lease that is already over");
        (await LoadAsync(tenant, message.Id)).LeasedUntil.Should().BeNull();
    }

    [Fact]
    public async Task A_conflict_on_the_final_save_does_not_stop_the_rest_of_the_batch()
    {
        var tenant = NewTenant();
        var passStart = DateTimeOffset.UtcNow.AddMinutes(-10);
        var first = await SeedAsync(tenant, passStart.AddMinutes(-5));
        var second = await SeedAsync(tenant, passStart.AddMinutes(-4));

        var transport = new FakeTransport(async (sent, ct) =>
        {
            if (sent.Id == first.Id)
            {
                await using var other = Store().LightweightSession(tenant);
                var copy = (await other.LoadAsync<OutboxMessage>(sent.Id, ct))!;
                copy.LastError = "touched by another node";
                other.Update(copy);
                await other.SaveChangesAsync(ct);
            }

            return OutboxSendResult.Ok(200, 1);
        });

        await using var session = Store().LightweightSession(tenant);
        var delivered = await Sender(transport).DeliverDueAsync(session, passStart, TestContext.Current.CancellationToken);

        delivered.Should().Be(1, "only the second message was recorded as delivered by this pass");
        (await LoadAsync(tenant, second.Id)).Status.Should().Be(OutboxStatus.Delivered);
        (await LoadAsync(tenant, first.Id)).Status.Should().Be(OutboxStatus.Pending);
    }

    private sealed class FakeTransport(Func<OutboxMessage, CancellationToken, Task<OutboxSendResult>> send) : IOutboxTransport
    {
        public Task<OutboxSendResult> SendAsync(OutboxMessage message, CancellationToken ct) => send(message, ct);
    }
}
