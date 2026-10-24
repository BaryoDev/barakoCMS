using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Notifications;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>Raising a notification, by a module through the service and by a client over HTTP.</summary>
[Collection("Integration1")]
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
        stored.Payload!["entryId"].ToString().Should().Be("JE-1");
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

    [Fact]
    public async Task A_recipient_with_no_membership_in_the_current_tenant_is_refused_and_nothing_is_stored_or_emailed()
    {
        var home = await _callers.TenantAsync();
        var elsewhere = await _callers.TenantAsync();
        var outsider = await _callers.UserAsync(null, elsewhere);
        var host = _factory.WithSettings(new Dictionary<string, string?> { ["Modules:Notifications:EmailDelivery:Enabled"] = "true" });

        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = home;
            var raise = () => scope.ServiceProvider.GetRequiredService<INotificationService>()
                .RaiseAsync(new RaiseNotification(outsider, "phish.me", "Click here"), Ct);
            await raise.Should().ThrowAsync<RecipientNotInTenantException>();
        }

        await using var read = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await read.Query<Notification>().Where(n => n.AnyTenant() && n.RecipientUserId == outsider).ToListAsync(Ct))
            .Should().BeEmpty();
        host.Services.GetRequiredService<RecordingEmailService>().Messages
            .Should().NotContain(m => m.To == $"ntf-{outsider:n}@example.com");
    }

    [Fact]
    public async Task Raising_over_http_for_a_user_of_another_tenant_is_not_found()
    {
        var home = await _callers.TenantAsync();
        var elsewhere = await _callers.TenantAsync();
        var outsider = await _callers.UserAsync(null, elsewhere);
        var admin = await _callers.SuperAdminAsync(home);

        var response = await admin.PostAsJsonAsync("/api/notifications",
            new { recipientUserId = outsider, type = "task.due", title = "Due" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_payload_over_8_KB_is_refused_over_http_and_through_the_service()
    {
        var recipient = await _callers.UserAsync();
        var big = new Dictionary<string, object> { ["blob"] = new string('x', 9000) };
        var admin = await _callers.SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/notifications",
            new { recipientUserId = recipient, type = "task.due", title = "Due", payload = big }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var raise = () => scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RaiseAsync(new RaiseNotification(recipient, "task.due", "Due") { Payload = big }, Ct);
        await raise.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("task.due\r\nBcc: x@example.com", "Due")]
    [InlineData("task.due", "Due\nBcc: x@example.com")]
    [InlineData("task\tdue", "Due")]
    [InlineData("task.due", "Du\u0000e")]
    [InlineData("task.due", "Due \u202Efdp.exe")]
    [InlineData("task.\u200Bdue", "Due")]
    public async Task A_type_or_title_with_a_control_character_is_refused_over_http_and_through_the_service(string type, string title)
    {
        var recipient = await _callers.UserAsync();
        var admin = await _callers.SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/notifications", new { recipientUserId = recipient, type, title }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var raise = () => scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RaiseAsync(new RaiseNotification(recipient, type, title), Ct);
        await raise.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_notification_body_is_html_encoded_in_the_email()
    {
        var recipient = await _callers.UserAsync();
        var address = $"ntf-{recipient:n}@example.com";
        var host = _factory.WithSettings(new Dictionary<string, string?> { ["Modules:Notifications:EmailDelivery:Enabled"] = "true" });

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(
                new RaiseNotification(recipient, "phish.test", "Look") { Body = "<a href=\"http://evil.example\">Click</a>\nsecond line" }, Ct);
        }

        var sent = host.Services.GetRequiredService<RecordingEmailService>().Messages.Single(m => m.To == address);
        sent.Body.Should().NotContain("<a href");
        sent.Body.Should().Contain("&lt;a href=");
        sent.Body.Should().Contain("<br>");
    }

    [Theory]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("keys")]
    public async Task The_service_enforces_the_same_length_and_key_caps_as_the_endpoint(string field)
    {
        var recipient = await _callers.UserAsync();
        var request = field switch
        {
            "type" => new RaiseNotification(recipient, new string('t', 101), "Due"),
            "title" => new RaiseNotification(recipient, "task.due", new string('x', 201)),
            "body" => new RaiseNotification(recipient, "task.due", "Due") { Body = new string('x', 4001) },
            _ => new RaiseNotification(recipient, "task.due", "Due")
            {
                Payload = Enumerable.Range(0, 51).ToDictionary(i => $"k{i}", _ => (object)1),
            },
        };

        using var scope = _factory.Services.CreateScope();
        var raise = () => scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(request, Ct);

        await raise.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_payload_with_a_number_and_a_nested_object_survives_a_raise_then_list_round_trip()
    {
        var recipient = await _callers.UserAsync();
        var admin = await _callers.SuperAdminAsync();
        var payload = new Dictionary<string, object>
        {
            ["amount"] = 125.5,
            ["entry"] = new Dictionary<string, object> { ["number"] = "JE-7", ["lines"] = 3 },
        };

        var created = await admin.PostAsJsonAsync("/api/notifications",
            new { recipientUserId = recipient, type = "entry.posted", title = "Posted", payload }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var items = (await NotificationCallers.ListAsync(_callers.ClientFor(recipient))).GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var back = items[0].GetProperty("payload");
        back.GetProperty("amount").GetDouble().Should().Be(125.5);
        back.GetProperty("entry").GetProperty("number").GetString().Should().Be("JE-7");
        back.GetProperty("entry").GetProperty("lines").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task A_sink_that_never_returns_is_cut_off_and_the_raise_completes()
    {
        var host = _factory.WithSettings(new Dictionary<string, string?> { ["Modules:Notifications:SinkTimeoutSeconds"] = "1" })
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<INotificationSink>(new HangingSink())));
        var recipient = await _callers.UserAsync();

        using var scope = host.Services.CreateScope();
        var raise = scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RaiseAsync(new RaiseNotification(recipient, "ticket.assigned", "Ticket 12 is yours"), Ct);
        var id = await raise.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await using var read = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await read.LoadAsync<Notification>(id, Ct)).Should().NotBeNull();
    }

    private sealed class HangingSink : INotificationSink
    {
        public Task DeliverAsync(Notification notification, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
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
