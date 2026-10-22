using BarakoCMS.Services;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Services;

public class ServiceHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static ServiceRegistration Service(TimeSpan sinceHeartbeat, int interval = 60) => new()
    {
        Name = "billing",
        HeartbeatIntervalSeconds = interval,
        CreatedAt = Now.AddDays(-30),
        LastHeartbeatAt = Now - sinceHeartbeat,
    };

    [Theory]
    [InlineData(0, "Healthy")]
    [InlineData(60, "Healthy")]
    [InlineData(61, "Late")]
    [InlineData(179, "Late")]
    [InlineData(180, "Down")]
    [InlineData(86_400, "Down")]
    public void A_service_is_judged_by_how_many_intervals_it_has_been_silent(int seconds, string expected)
    {
        ServiceHealth.Evaluate(Service(TimeSpan.FromSeconds(seconds)), Now).ToString().Should().Be(expected);
    }

    [Fact]
    public void The_interval_is_the_services_own()
    {
        var service = Service(TimeSpan.FromSeconds(120), interval: 300);

        ServiceHealth.Evaluate(service, Now).Should().Be(ServiceHealthState.Healthy,
            "two minutes is inside a five minute interval, though it is Late for the default of one");
    }

    [Fact]
    public void A_service_that_never_reported_is_measured_from_its_registration()
    {
        var fresh = new ServiceRegistration { CreatedAt = Now.AddSeconds(-10), HeartbeatIntervalSeconds = 60 };
        var old = new ServiceRegistration { CreatedAt = Now.AddHours(-1), HeartbeatIntervalSeconds = 60 };

        ServiceHealth.Evaluate(fresh, Now).Should().Be(ServiceHealthState.Healthy);
        ServiceHealth.Evaluate(old, Now).Should().Be(ServiceHealthState.Down);
    }
}
