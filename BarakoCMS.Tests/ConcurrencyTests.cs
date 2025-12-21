using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using FastEndpoints.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ConcurrencyTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public ConcurrencyTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(string token, Guid userId)> CreateAdminUserAsync()
    {
        return await TestHelpers.CreateAdminUserAsync(_factory);
    }

    [Fact]
    public async Task UpdateContent_ShouldFail_WhenVersionMismatch()
    {
        // Arrange: Create Admin User
        var (token, userId) = await CreateAdminUserAsync();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create Content (no ContentType definition needed - loose mode validation)
        var contentData = new Dictionary<string, object> { { "Title", "Original Title" } };
        var createReq = new
        {
            ContentType = "Article",
            Data = contentData
        };
        var createResp = await _client.PostAsJsonAsync("/api/contents", createReq);
        createResp.EnsureSuccessStatusCode();
        var createData = await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>();
        var contentId = createData!.Id;

        // First Update: Client thinks content is at version 1 (after creation)
        var updateReq1 = new
        {
            Id = contentId,
            Data = new Dictionary<string, object> { { "Title", "Updated Title 1" } },
            Version = 1 // Content is at v1 after creation
        };
        var resp1 = await _client.PutAsJsonAsync($"/api/contents/{contentId}", updateReq1);
        resp1.EnsureSuccessStatusCode(); // This should succeed, moving content to v2

        // Second Update: Client STILL thinks content is at v1 (stale version)
        var updateReq2 = new
        {
            Id = contentId,
            Data = new Dictionary<string, object> { { "Title", "Updated Title 2" } },
            Version = 1 // Stale! Content is now at v2
        };
        var resp2 = await _client.PutAsJsonAsync($"/api/contents/{contentId}", updateReq2);

        // Assert: Should fail with 412 PreconditionFailed due to version mismatch
        resp2.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        var error = await resp2.Content.ReadAsStringAsync();
        error.Should().Contain("modified by another user");
    }
}
