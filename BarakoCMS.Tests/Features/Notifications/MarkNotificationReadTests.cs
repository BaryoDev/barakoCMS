using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Features.Notifications.NotificationCallers;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>Marking read: only the recipient can, and only their own.</summary>
[Collection("Integration4")]
public class MarkNotificationReadTests
{
    private readonly NotificationCallers _callers;

    public MarkNotificationReadTests(IntegrationTestFixture factory) => _callers = new NotificationCallers(factory);

    [Fact]
    public async Task The_recipient_marks_a_notification_read_and_it_stays_read()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var id = await RaiseAsync(admin, ana);
        var client = _callers.ClientFor(ana);

        var first = await client.PostAsync($"/api/notifications/{id}/read", null, Ct);
        var firstItems = (await ListAsync(client)).GetProperty("items");
        firstItems.GetArrayLength().Should().Be(1);
        var readAt = firstItems[0].GetProperty("readAt").GetDateTimeOffset();
        var again = await client.PostAsync($"/api/notifications/{id}/read", null, Ct);
        var againItems = (await ListAsync(client)).GetProperty("items");
        againItems.GetArrayLength().Should().Be(1);
        var readAtAgain = againItems[0].GetProperty("readAt").GetDateTimeOffset();

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent, "marking read twice is not an error");
        readAtAgain.Should().Be(readAt, "the first read time is kept");
    }

    [Fact]
    public async Task Another_user_cannot_mark_it_read_and_it_stays_unread()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var ben = await _callers.UserAsync();
        var id = await RaiseAsync(admin, ana);

        var response = await _callers.ClientFor(ben).PostAsync($"/api/notifications/{id}/read", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "someone else's notification is not found, not forbidden");
        var items = (await ListAsync(_callers.ClientFor(ana))).GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var item = items[0];
        item.GetProperty("readAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Marking_an_unknown_notification_read_is_not_found()
    {
        var response = await _callers.ClientFor(await _callers.UserAsync())
            .PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mark_all_read_touches_only_the_callers_notifications()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var ben = await _callers.UserAsync();
        await RaiseAsync(admin, ana);
        await RaiseAsync(admin, ana);
        await RaiseAsync(admin, ben);

        var response = await _callers.ClientFor(ana).PostAsync("/api/notifications/read-all", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("updated").GetInt32().Should().Be(2);
        Ids(await ListAsync(_callers.ClientFor(ana), "?unreadOnly=true")).Should().BeEmpty();
        Ids(await ListAsync(_callers.ClientFor(ben), "?unreadOnly=true")).Should().HaveCount(1, "Ben's was not marked");
    }
}
