extern alias App;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Net;
using barakoCMS.Models;
using FastEndpoints.Security;
using System.Net.Http.Headers;
using Xunit;

namespace AttendancePOC.Tests;

public class ExtraFeatureTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ExtraFeatureTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Idempotency_ShouldReturnConflict_OnDuplicateKey()
    {
        // 1. Authenticate
        var token = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("Admin");
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 2. Prepare Data
        var key = Guid.NewGuid().ToString();
        _client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var req = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = new Dictionary<string, object>
            {
                { "Name", "Idempotency Test" }
            },
            Status = ContentStatus.Published
        };

        // 3. First Request - Should Success
        var res1 = await _client.PostAsJsonAsync("/api/contents", req);
        res1.EnsureSuccessStatusCode();

        // 4. Second Request - Should Fail or Return Cached (We'll implement 409 Conflict for simplicity or 200 with same body)
        // For this POC, let's assume we implement 409 Conflict for now as "Already Processed"
        var res2 = await _client.PostAsJsonAsync("/api/contents", req);

        // Assert
        res2.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task History_ShouldTrackChanges_And_AllowRollback()
    {
        // 1. Authenticate
        var token = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("Admin");
                u.Roles.Add("SuperAdmin");
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 2. Create Content (Version 1)
        var req = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = new Dictionary<string, object>
            {
                { "Name", "Version 1" }
            },
            Status = ContentStatus.Draft
        };

        var createRes = await _client.PostAsJsonAsync("/api/contents", req);
        createRes.EnsureSuccessStatusCode();
        var contentId = (await createRes.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>())!.Id;

        // 3. Update Content (Version 2)
        var updateReq = new barakoCMS.Features.Content.Update.Request
        {
            Id = contentId,
            Data = new Dictionary<string, object>
            {
                { "Name", "Version 2" },
                { "Updated", true }
            }
        };
        var updateRes = await _client.PutAsJsonAsync($"/api/contents/{contentId}", updateReq);
        updateRes.EnsureSuccessStatusCode();

        // 4. Verify History
        var historyRes = await _client.GetAsync($"/api/contents/{contentId}/history");
        historyRes.EnsureSuccessStatusCode();
        var historyResponse = await historyRes.Content.ReadFromJsonAsync<barakoCMS.Features.Content.History.Response>();

        historyResponse.Should().NotBeNull();
        historyResponse!.Versions.Count.Should().BeGreaterThanOrEqualTo(2);

        // 5. Rollback to Version 1
        // The versions are likely in some order (e.g. chronological). 
        // Let's find the version where Name == "Version 1".
        var version1 = historyResponse.Versions.FirstOrDefault(v => v.Data["Name"].ToString() == "Version 1");
        version1.Should().NotBeNull();

        var rollbackRes = await _client.PostAsJsonAsync($"/api/contents/{contentId}/rollback/{version1!.VersionId}", new { });
        rollbackRes.EnsureSuccessStatusCode();

        // 6. Verify Content is Reverted
        var currentContentRes = await _client.GetFromJsonAsync<barakoCMS.Models.Content>($"/api/contents/{contentId}"); // Provided there is a Get endpoint?
        // Actually, Rollback endpoint returns the new content state!
        var rolledBackContent = await rollbackRes.Content.ReadFromJsonAsync<barakoCMS.Models.Content>();

        rolledBackContent.Should().NotBeNull();
        rolledBackContent!.Data["Name"].ToString().Should().Be("Version 1");
        // And ensure "Updated" key is gone or matches version 1 (which didn't have it)
        rolledBackContent.Data.ContainsKey("Updated").Should().BeFalse();
    }
}
