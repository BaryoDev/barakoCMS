using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Features.Notifications.NotificationCallers;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>Listing: a user sees their own notifications and nobody else's, in bounded pages.</summary>
[Collection("Sequential")]
public class ListNotificationTests
{
    private readonly NotificationCallers _callers;

    public ListNotificationTests(IntegrationTestFixture factory) => _callers = new NotificationCallers(factory);

    [Fact]
    public async Task A_user_lists_only_their_own_notifications()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var ben = await _callers.UserAsync();
        var forAna = await RaiseAsync(admin, ana);
        var forBen = await RaiseAsync(admin, ben);

        var anaSees = Ids(await ListAsync(_callers.ClientFor(ana)));
        var benSees = Ids(await ListAsync(_callers.ClientFor(ben)));

        anaSees.Should().HaveCount(1);
        anaSees.Should().Equal(forAna);
        benSees.Should().HaveCount(1);
        benSees.Should().Equal(forBen);
    }

    [Fact]
    public async Task Listing_is_paged_and_the_page_size_is_capped()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        for (var i = 0; i < 3; i++) await RaiseAsync(admin, ana, title: $"n{i}");
        var client = _callers.ClientFor(ana);

        var first = await ListAsync(client, "?pageSize=2&page=1");
        var second = await ListAsync(client, "?pageSize=2&page=2");
        var huge = await ListAsync(client, "?pageSize=100000");

        first.GetProperty("totalItems").GetInt32().Should().Be(3);
        Ids(first).Should().HaveCount(2);
        Ids(second).Should().HaveCount(1);
        Ids(first).Intersect(Ids(second)).Should().BeEmpty();
        huge.GetProperty("pageSize").GetInt32().Should().Be(100, "no caller can ask for an unbounded page");
    }

    [Fact]
    public async Task Unread_only_leaves_out_what_was_read_and_the_count_follows()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var read = await RaiseAsync(admin, ana);
        var unread = await RaiseAsync(admin, ana);
        var client = _callers.ClientFor(ana);
        (await client.PostAsync($"/api/notifications/{read}/read", null, Ct)).EnsureSuccessStatusCode();

        var all = Ids(await ListAsync(client));
        var onlyUnread = Ids(await ListAsync(client, "?unreadOnly=true"));
        var count = await client.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count", Ct);

        all.Should().HaveCount(2);
        onlyUnread.Should().Equal(unread);
        count.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Listing_needs_a_signed_in_user()
    {
        var response = await _callers.AnonymousGetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_admin_list_shows_every_recipient_in_the_tenant_and_needs_the_capability()
    {
        var admin = await _callers.SuperAdminAsync();
        var ana = await _callers.UserAsync();
        var ben = await _callers.UserAsync();
        var forAna = await RaiseAsync(admin, ana);
        var forBen = await RaiseAsync(admin, ben);

        var all = Ids(await AdminListAsync(admin, "?pageSize=100"));
        var onlyBen = Ids(await AdminListAsync(admin, $"?recipientUserId={ben}"));
        var asUser = await _callers.ClientFor(ana).GetAsync("/api/notifications/admin", Ct);

        all.Should().Contain([forAna, forBen]);
        onlyBen.Should().Equal(forBen);
        asUser.StatusCode.Should().Be(HttpStatusCode.Forbidden, "reading everyone's notifications is not for an ordinary user");
    }

    private static async Task<JsonElement> AdminListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/notifications/admin{query}", Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }
}
