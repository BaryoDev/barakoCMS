using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using FluentAssertions;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ContentRollbackTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public ContentRollbackTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Rollback_UpdatesReadModel_SoGetReturnsRolledBackData()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // v1
        var createResp = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "v1" } }
        });
        createResp.EnsureSuccessStatusCode();
        var contentId = (await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>())!.Id;

        // v2
        var updateResp = await _client.PutAsJsonAsync($"/api/contents/{contentId}", new
        {
            Id = contentId,
            Data = new Dictionary<string, object> { { "Title", "v2" } },
            Version = 1
        });
        updateResp.EnsureSuccessStatusCode();

        // Grab the history and find the original (v1) version.
        var historyResp = await _client.GetAsync($"/api/contents/{contentId}/history");
        historyResp.EnsureSuccessStatusCode();
        var history = await historyResp.Content.ReadFromJsonAsync<JsonElement>();
        var versions = history.GetProperty("versions").EnumerateArray().ToList();

        Guid v1VersionId = default;
        foreach (var v in versions)
        {
            var title = v.GetProperty("data").GetProperty("Title").GetString();
            if (title == "v1")
            {
                v1VersionId = v.GetProperty("versionId").GetGuid();
            }
        }
        v1VersionId.Should().NotBe(default(Guid), "the v1 version should appear in history");

        // Roll back to v1.
        var rollbackResp = await _client.PostAsJsonAsync($"/api/contents/{contentId}/rollback/{v1VersionId}", new { });
        rollbackResp.EnsureSuccessStatusCode();

        // The read model (what GET returns) must now reflect the rolled-back data, not the stale v2.
        var getResp = await _client.GetAsync($"/api/contents/{contentId}");
        getResp.EnsureSuccessStatusCode();
        var content = await getResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Get.Response>();
        content!.Data["Title"].ToString().Should().Contain("v1");
    }
}
