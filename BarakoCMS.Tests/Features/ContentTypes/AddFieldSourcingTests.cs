using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// Adding a field to an event-sourced type keeps the rule the type was created under: its fields
/// stay Public.
/// </summary>
/// <remarks>
/// Each refusal has its pairs, because there are two ways it could pass for the wrong reason: an
/// endpoint that refuses every field on an event-sourced type, and one that refuses every field
/// that is not Public.
/// </remarks>
[Collection("Sequential")]
public class AddFieldSourcingTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public AddFieldSourcingTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Created through the API, so the sourcing decision is recorded the way it is in use.</summary>
    private async Task<string> TypeAsync(bool eventSourced)
    {
        var name = "addfield-es-" + Guid.NewGuid().ToString("n")[..12];

        var created = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Add field sourcing probe",
            eventSourced,
            fields = new[] { new { name = "Title", type = "string", sensitivity = "Public" } },
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        return name;
    }

    private async Task<ContentTypeDefinition> ReadAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type))!;
    }

    [Theory]
    [InlineData("Sensitive")]
    [InlineData("Hidden")]
    public async Task A_field_that_is_not_public_is_refused_on_an_event_sourced_type(string sensitivity)
    {
        var type = await TypeAsync(eventSourced: true);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Secret",
            type = "string",
            sensitivity,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the status the sensitivity endpoint answers with");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("is event sourced, so its fields have to stay Public");
        body.Should().Contain("Secret", "the refusal names the field");
        body.Should().Contain(sensitivity, "and the level it was refused at");

        var definition = await ReadAsync(type);
        definition.Fields.Should().HaveCount(1, "a refused field is not stored");
        definition.Fields.Select(f => f.Name).Should().NotContain("Secret");
    }

    [Fact]
    public async Task A_public_field_is_still_added_to_an_event_sourced_type()
    {
        var type = await TypeAsync(eventSourced: true);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Subtitle",
            type = "string",
            sensitivity = "Public",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var definition = await ReadAsync(type);
        definition.Fields.Should().HaveCount(2, "the type started with one field and gained one");
        definition.Fields.Single(f => f.Name == "Subtitle").Sensitivity.Should().Be(SensitivityLevel.Public);
    }

    [Theory]
    [InlineData("Sensitive", SensitivityLevel.Sensitive)]
    [InlineData("Hidden", SensitivityLevel.Hidden)]
    public async Task A_field_that_is_not_public_is_still_added_to_a_type_that_is_not_event_sourced(
        string sensitivity, SensitivityLevel stored)
    {
        var type = await TypeAsync(eventSourced: false);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Secret",
            type = "string",
            sensitivity,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var definition = await ReadAsync(type);
        definition.Fields.Should().HaveCount(2, "the type started with one field and gained one");
        definition.Fields.Single(f => f.Name == "Secret").Sensitivity.Should().Be(stored);
    }
}
