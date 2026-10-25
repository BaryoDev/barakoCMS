using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Pwa;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BarakoCMS.Tests.Features.Pwa;

/// <summary>
/// The platform a device reports is one of a fixed set, and a device not seen for the retention
/// window is deleted.
/// </summary>
[Collection("Integration1")]
public class PwaPlatformAndRetentionTests(IntegrationTestFixture factory)
{
    private async Task<PwaInstall?> LoadAsync(string deviceId)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<PwaInstall>().ToListAsync(TestContext.Current.CancellationToken))
            .FirstOrDefault(p => p.DeviceId == deviceId);
    }

    [Theory]
    [InlineData("iOS", "ios")]
    [InlineData("macos", "macos")]
    [InlineData("ChromeOS", "other")]
    [InlineData("<script>alert(1)</script>", "other")]
    public async Task A_reported_platform_is_stored_as_one_of_the_known_values(string sent, string stored)
    {
        var device = $"dev-{Guid.NewGuid():N}";

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/pwa/report", new { deviceId = device, platform = sent }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "an unknown value is mapped, not refused");
        (await LoadAsync(device))!.Platform.Should().Be(stored);
    }

    [Fact]
    public async Task A_report_without_a_platform_stores_none()
    {
        var device = $"dev-{Guid.NewGuid():N}";

        await factory.CreateClient().PostAsJsonAsync(
            "/api/pwa/report", new { deviceId = device }, TestContext.Current.CancellationToken);

        (await LoadAsync(device))!.Platform.Should().BeNull();
    }

    private async Task<(string Stale, string Recent)> SeedDevicesAsync()
    {
        var stale = $"stale-{Guid.NewGuid():N}";
        var recent = $"recent-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new PwaInstall { DeviceId = stale, LastSeenAt = DateTime.UtcNow.AddDays(-200) });
        session.Store(new PwaInstall { DeviceId = recent, LastSeenAt = DateTime.UtcNow.AddDays(-20) });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (stale, recent);
    }

    private static PwaRetentionService SweeperOf(IServiceProvider services)
    {
        var service = services.GetServices<IHostedService>().OfType<PwaRetentionService>().ToList();
        service.Should().HaveCount(1);
        return service[0];
    }

    [Fact]
    public async Task The_sweep_deletes_devices_not_seen_within_the_configured_window_and_keeps_the_rest()
    {
        var (stale, recent) = await SeedDevicesAsync();
        (await LoadAsync(stale)).Should().NotBeNull("the control: the row exists before the sweep");

        var host = factory.WithSetting("Modules:Pwa:RetentionDays", "180");
        await SweeperOf(host.Services).SweepAsync(DateTime.UtcNow, TestContext.Current.CancellationToken);

        (await LoadAsync(stale)).Should().BeNull("200 days is past the 180 day window");
        (await LoadAsync(recent)).Should().NotBeNull();
    }

    [Fact]
    public async Task Without_a_retention_setting_the_sweep_deletes_nothing()
    {
        var (stale, recent) = await SeedDevicesAsync();
        (await LoadAsync(stale)).Should().NotBeNull("the control: the row exists before the sweep");

        await SweeperOf(factory.Services).SweepAsync(DateTime.UtcNow, TestContext.Current.CancellationToken);

        (await LoadAsync(stale)).Should().NotBeNull("retention is off until it is set");
        (await LoadAsync(recent)).Should().NotBeNull();
    }

    [Fact]
    public void Retention_is_off_by_default()
    {
        new PwaOptions().RetentionDays.Should().Be(0);
    }
}
