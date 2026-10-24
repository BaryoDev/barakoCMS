using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.Features.Notifications.NotificationCallers;

namespace BarakoCMS.Tests.Features.Notifications;

/// <summary>A notification raised in one tenant is invisible from another, even to the same user.</summary>
[Collection("Integration3")]
public class NotificationTenantIsolationTests
{
    private readonly NotificationCallers _callers;

    public NotificationTenantIsolationTests(IntegrationTestFixture factory) => _callers = new NotificationCallers(factory);

    [Fact]
    public async Task A_user_in_two_tenants_sees_each_tenants_notifications_apart()
    {
        var clinic = await _callers.TenantAsync();
        var school = await _callers.TenantAsync();
        var ana = await _callers.UserAsync(null, clinic, school);
        var inClinic = await RaiseAsync(await _callers.SuperAdminAsync(clinic), ana);
        var inSchool = await RaiseAsync(await _callers.SuperAdminAsync(school), ana);

        var clinicSees = Ids(await ListAsync(_callers.ClientFor(ana, clinic)));
        var schoolSees = Ids(await ListAsync(_callers.ClientFor(ana, school)));

        clinicSees.Should().Equal(inClinic);
        schoolSees.Should().Equal(inSchool);
    }

    [Fact]
    public async Task A_notification_of_another_tenant_cannot_be_marked_read_from_this_one()
    {
        var clinic = await _callers.TenantAsync();
        var school = await _callers.TenantAsync();
        var ana = await _callers.UserAsync(null, clinic, school);
        var inClinic = await RaiseAsync(await _callers.SuperAdminAsync(clinic), ana);

        var response = await _callers.ClientFor(ana, school).PostAsync($"/api/notifications/{inClinic}/read", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListAsync(_callers.ClientFor(ana, clinic), "?unreadOnly=true")).GetProperty("totalItems").GetInt32()
            .Should().Be(1);
    }

    [Fact]
    public async Task The_admin_list_of_one_tenant_does_not_show_another_tenants_notifications()
    {
        var clinic = await _callers.TenantAsync();
        var school = await _callers.TenantAsync();
        var ana = await _callers.UserAsync(null, clinic, school);
        var clinicAdmin = await _callers.SuperAdminAsync(clinic);
        var schoolAdmin = await _callers.SuperAdminAsync(school);
        var inClinic = await RaiseAsync(clinicAdmin, ana);
        var inSchool = await RaiseAsync(schoolAdmin, ana);

        var fromSchool = await schoolAdmin.GetAsync($"/api/notifications/admin?recipientUserId={ana}", Ct);

        fromSchool.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = Ids(JsonDocument.Parse(await fromSchool.Content.ReadAsStringAsync(Ct)).RootElement);
        ids.Should().Equal(inSchool);
        ids.Should().NotContain(inClinic);
    }
}
