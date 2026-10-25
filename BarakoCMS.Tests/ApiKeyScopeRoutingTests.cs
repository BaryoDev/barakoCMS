using barakoCMS.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class ApiKeyScopeRoutingTests
{
    [Theory]
    [InlineData("/api/monitoring/health")]
    [InlineData("/API/Monitoring/Health/")]
    public void Get_health_needs_monitor_read(string path) =>
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("monitor:read");

    [Theory]
    [InlineData("/api/monitoring/health")]
    [InlineData("/api/monitoring/health/")]
    public void A_write_to_health_is_refused(string path)
    {
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: true).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/monitoring/health/x")]
    [InlineData("/api/monitoring/metrics")]
    [InlineData("/api/monitoring/k8s")]
    [InlineData("/api/monitoring")]
    public void Other_monitoring_paths_are_refused(string path)
    {
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().BeNull();
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: true).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/overview")]
    [InlineData("/API/Overview/")]
    public void Get_overview_needs_monitor_read(string path) =>
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("monitor:read");

    [Fact]
    public void A_write_to_overview_is_refused() =>
        ApiKeyScopeProcessor.RequiredScopes("/api/overview", isWrite: true).Should().BeNull();

    [Theory]
    [InlineData("/api/overview/items/mail.bounces/resolve")]
    [InlineData("/api/overview/x")]
    public void Overview_subpaths_are_refused(string path)
    {
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().BeNull();
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: true).Should().BeNull();
    }
}
