using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ContentSensitivityTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public ContentSensitivityTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Content_created_as_Sensitive_is_stored_as_Sensitive()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createResp = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "classified" } },
            Sensitivity = barakoCMS.Models.SensitivityLevel.Sensitive
        });
        createResp.EnsureSuccessStatusCode();
        var id = (await createResp.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>())!.Id;

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<barakoCMS.Models.Content>(id);

        stored.Should().NotBeNull();
        stored!.Sensitivity.Should().Be(
            barakoCMS.Models.SensitivityLevel.Sensitive,
            "the API accepts a Sensitivity on the create request, so document-level masking must actually engage");
    }
}
