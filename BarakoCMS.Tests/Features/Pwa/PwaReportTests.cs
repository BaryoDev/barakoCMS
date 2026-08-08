using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using BarakoCMS.Pwa;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Pwa;

/// <summary>
/// POST /api/pwa/report. The endpoint is anonymous because a launch happens before sign-in, which
/// means most launch reports arrive with no identity — an expired access token is accepted rather
/// than refused. Attribution therefore depends on a later authenticated report for the same device
/// backfilling the user onto the existing row. That backfill is the contract these pin down.
/// </summary>
[Collection("Sequential")]
public class PwaReportTests
{
    private readonly IntegrationTestFixture _factory;

    public PwaReportTests(IntegrationTestFixture factory) => _factory = factory;

    private static object Report(string deviceId, string displayMode = "standalone", bool installed = true) =>
        new { deviceId, displayMode, platform = "macos", installed };

    private async Task<PwaInstall?> LoadAsync(string deviceId)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await s.Query<PwaInstall>().ToListAsync()).FirstOrDefault(p => p.DeviceId == deviceId);
    }

    [Fact]
    public async Task Anonymous_report_is_recorded_without_a_user()
    {
        var device = $"dev-{Guid.NewGuid():N}";

        var res = await _factory.CreateClient().PostAsJsonAsync("/api/pwa/report", Report(device));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = await LoadAsync(device);
        row.Should().NotBeNull();
        row!.Installed.Should().BeTrue();
        row.Username.Should().BeNull("an anonymous launch has nobody to attribute it to");
    }

    [Fact]
    public async Task A_later_authenticated_report_backfills_the_user()
    {
        var device = $"dev-{Guid.NewGuid():N}";
        var anon = _factory.CreateClient();

        // The launch report, as it actually arrives in production: no usable token.
        await anon.PostAsJsonAsync("/api/pwa/report", Report(device));
        (await LoadAsync(device))!.UserId.Should().BeNull();

        // The client re-reports once signed in. Same device, so it must update rather than duplicate.
        // The token carries both UserId and Username, as a real one minted by TokenIssuer does.
        var (_, userId) = await TestHelpers.CreateAdminUserAsync(_factory);
        var token = _factory.CreateToken(
            new[] { "SuperAdmin" },
            userId.ToString(),
            new Dictionary<string, string> { ["Username"] = "pwa-tester" });
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await authed.PostAsJsonAsync("/api/pwa/report", Report(device));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = await LoadAsync(device);
        row!.UserId.Should().Be(userId, "the install is now attributable");
        row.Username.Should().Be("pwa-tester");
        row.LaunchCount.Should().Be(2, "the same device is one row with a launch count, not two rows");
    }

    [Fact]
    public async Task Install_time_is_kept_from_the_first_install()
    {
        var device = $"dev-{Guid.NewGuid():N}";
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/api/pwa/report", Report(device));
        var first = (await LoadAsync(device))!.InstalledAt;

        await client.PostAsJsonAsync("/api/pwa/report", Report(device));

        (await LoadAsync(device))!.InstalledAt.Should().Be(first, "re-reporting must not reset when it was installed");
    }

    [Fact]
    public async Task A_browser_launch_is_not_counted_as_installed()
    {
        var device = $"dev-{Guid.NewGuid():N}";

        await _factory.CreateClient().PostAsJsonAsync("/api/pwa/report",
            Report(device, displayMode: "browser", installed: false));

        (await LoadAsync(device))!.Installed.Should().BeFalse();
    }

    [Fact]
    public async Task DeviceId_is_required()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/pwa/report", Report(""));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
