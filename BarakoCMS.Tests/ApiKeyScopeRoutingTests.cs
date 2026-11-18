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

    [Theory]
    [InlineData("/api/pages/tree")]
    [InlineData("/API/Pages/Tree/")]
    [InlineData("/api/blocks/schema")]
    public void Authoring_reads_need_content_read(string path) =>
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("content:read");

    [Theory]
    [InlineData("/api/pages/tree")]
    [InlineData("/api/blocks/schema")]
    public void Authoring_read_paths_stay_closed_to_key_writes(string path) =>
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: true).Should().BeNull();

    [Theory]
    [InlineData("/api/contents/types")]
    [InlineData("/api/contents/types/post")]
    [InlineData("/api/contents/types/erase")]
    [InlineData("/api/contents/types/share-links")]
    [InlineData("/api/contents/types/rollback")]
    public void The_types_route_is_a_plain_content_read(string path) =>
        ApiKeyScopeProcessor.RequiredScopes(path, isWrite: false).Should().Equal("content:read");

    [Theory]
    [InlineData("/api/pages/tree")]
    [InlineData("/api/blocks/schema")]
    [InlineData("/api/contents/types/post")]
    public void A_read_only_key_may_read_the_authoring_reads(string path) =>
        ApiKeyScopeProcessor.IsReadOnlyReadable(path).Should().BeTrue();

    [Fact]
    public void The_types_exclusion_leaves_real_destructive_and_share_link_routes_alone()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        ApiKeyScopeProcessor.RequiredScopes($"/api/contents/{id}/erase", isWrite: true)
            .Should().Equal(barakoCMS.Models.ApiKeyScopes.ContentDestructive);
        ApiKeyScopeProcessor.RequiredScopes($"/api/contents/{id}/rollback/{version}", isWrite: true)
            .Should().Equal(barakoCMS.Models.ApiKeyScopes.ContentDestructive);
        ApiKeyScopeProcessor.RequiredScopes($"/api/contents/{id}/share-links", isWrite: false)
            .Should().BeNull();
    }
}
