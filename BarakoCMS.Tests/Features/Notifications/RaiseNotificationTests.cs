using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Notifications;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>Raising a notification, by a module through the service and by a client over HTTP.</summary>
[Collection("Sequential")]
public class RaiseNotificationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly NotificationCallers _callers;

    public RaiseNotificationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _callers = new NotificationCallers(factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_module_raises_a_notification_through_the_service_and_it_is_stored_unread()
    {
        var recipient = await _callers.UserAsync();

        using var scope = _factory.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(
            new RaiseNotification(recipient, "entry.needs-approval", "Entry JE-1 needs approval")
            {
                Body = "Posted by Ana",
                Payload = new Dictionary<string, object> { ["entryId"] = "JE-1" },
            }, Ct);

        await using var read = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await read.LoadAsync<Notification>(id, Ct);

        stored.Should().NotBeNull();
        stored!.RecipientUserId.Should().Be(recipient);
        stored.Type.Should().Be("entry.needs-approval");
        stored.Title.Should().Be("Entry JE-1 needs approval");
        stored.Body.Should().Be("Posted by Ana");
        stored.ReadAt.Should().BeNull("a new notification is unread");
        stored.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        stored.Payload.Should().ContainKey("entryId");
    }

    [Fact]
    public async Task A_sink_is_handed_the_notification_beside_the_in_app_store()
    {
        var sink = new RecordingSink();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<INotificationSink>(sink)));
        var recipient = await _callers.UserAsync();

        using var scope = host.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RaiseAsync(new RaiseNotification(recipient, "lead.cold", "A lead went cold"), Ct);

        sink.Delivered.Should().ContainSingle();
        sink.Delivered[0].Id.Should().Be(id);

        await using var read = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await read.LoadAsync<Notification>(id, Ct)).Should().NotBeNull("the in-app store is always on");
    }

    [Fact]
    public async Task A_sink_that_throws_does_not_lose_the_notification_or_fail_the_raise()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<INotificationSink>(new ThrowingSink())));
        var recipient = await _callers.UserAsync();

        using var scope = host.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RaiseAsync(new RaiseNotification(recipient, "ticket.assigned", "Ticket 12 is yours"), Ct);

        await using var read = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await read.LoadAsync<Notification>(id, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Email_delivery_is_off_until_configured_and_then_emails_the_recipient()
    {
        var recipient = await _callers.UserAsync();
        var address = $"ntf-{recipient:n}@example.com";

        var off = _factory.WithSettings(new Dictionary<string, string?>());
        using (var scope = off.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationService>()
                .RaiseAsync(new RaiseNotification(recipient, "payment.approved", "Payment approved"), Ct);
        }
        off.Services.GetRequiredService<RecordingEmailService>().Messages
            .Should().NotContain(m => m.To == address);

        var on = _factory.WithSettings(new Dictionary<string, string?> { ["Modules:Notifications:EmailDelivery:Enabled"] = "true" });
        using (var scope = on.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationService>()
                .RaiseAsync(new RaiseNotification(recipient, "payment.approved", "Payment approved"), Ct);
        }
        on.Services.GetRequiredService<RecordingEmailService>().Messages
            .Should().Contain(m => m.To == address && m.Subject == "Payment approved");
    }

    [Fact]
    public async Task Raising_over_http_needs_the_manage_notifications_capability()
    {
        var recipient = await _callers.UserAsync();
        var body = new { recipientUserId = recipient, type = "task.due", title = "Due" };

        (await _factory.CreateClient().PostAsJsonAsync("/api/notifications", body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var plain = _callers.ClientFor(await _callers.UserAsync());
        (await plain.PostAsJsonAsync("/api/notifications", body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "an ordinary user cannot send notifications to others");

        var admin = await _callers.SuperAdminAsync();
        var created = await admin.PostAsJsonAsync("/api/notifications", body, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("", "A title")]
    [InlineData("task.due", "")]
    public async Task A_notification_without_a_type_or_a_title_is_refused(string type, string title)
    {
        var admin = await _callers.SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/notifications",
            new { recipientUserId = Guid.NewGuid(), type, title }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_notification_without_a_recipient_is_refused()
    {
        var admin = await _callers.SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/notifications",
            new { recipientUserId = Guid.Empty, type = "task.due", title = "Due" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class RecordingSink : INotificationSink
    {
        public List<Notification> Delivered { get; } = [];

        public Task DeliverAsync(Notification notification, CancellationToken ct)
        {
            Delivered.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : INotificationSink
    {
        public Task DeliverAsync(Notification notification, CancellationToken ct) =>
            throw new InvalidOperationException("the channel is down");
    }
}
