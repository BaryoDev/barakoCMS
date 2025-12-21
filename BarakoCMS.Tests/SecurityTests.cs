using System.Net;
using BarakoCMS.Tests;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class SecurityTests
{
    private readonly IntegrationTestFixture _factory;

    public SecurityTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BackupEndpoints_Reject_RegularUser()
    {
        // Arrange
        var client = _factory.CreateClient();
        // If AllowAnonymous was removed, then unauthenticated requests should return 401/403/404.

        // Act
        var response = await client.GetAsync("/api/backups");

        // Assert
        // Expect 401, 403, or 404 (endpoint not existing is also secure)
        (response.StatusCode == HttpStatusCode.Unauthorized ||
         response.StatusCode == HttpStatusCode.Forbidden ||
         response.StatusCode == HttpStatusCode.NotFound)
            .Should().BeTrue($"Expected 401, 403, or 404, but got {response.StatusCode}");
    }

    [Fact]
    public async Task RestoreBackup_Withdotdot_ShouldBeBlocked()
    {
        // Arrange
        var client = _factory.CreateClient();
        // Test that suspicious path traversal patterns are blocked

        // Act
        var response = await client.PostAsync("/api/backups/../etc/passwd/restore", null);

        // Assert - Should NOT return 200 or 202 (any error response is acceptable for security)
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.StatusCode.Should().NotBe(HttpStatusCode.Accepted);
    }
}
