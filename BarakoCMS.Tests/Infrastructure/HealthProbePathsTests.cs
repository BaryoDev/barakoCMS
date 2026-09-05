using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Health;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The routing predicate that keeps the health probes out of the output cache (#545). A kubelet
/// reading a stale cached ready response either kills a healthy pod or keeps sending traffic to a
/// broken one, so this is what <c>UseBarakoCMS</c> branches on before calling <c>UseOutputCache</c>.
/// </summary>
public class HealthProbePathsTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/build")]
    [InlineData("/HEALTH/READY")]
    public void A_health_path_is_recognised(string path) =>
        HealthProbePaths.IsHealthPath(path).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api/public/redirects/resolve")]
    [InlineData("/health-ui")]
    [InlineData("/health-ui-api")]
    [InlineData("/healthy")]
    public void Anything_else_is_not_a_health_path(string? path) =>
        HealthProbePaths.IsHealthPath(path).Should().BeFalse();
}
