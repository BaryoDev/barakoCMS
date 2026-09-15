using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The cap on how many fields one content type may hold (#650).
/// </summary>
/// <remarks>
/// A definition is returned whole by the content-types list, so an uncapped field list made one
/// create inflate every later list call, and no endpoint deletes a type to undo it. The cap refuses
/// growth past <see cref="ContentTypeFieldLimit.Default"/>. A type already stored over it keeps
/// working, which the last integration test pins down.
/// </remarks>
[Collection("Sequential")]
public class FieldCountCapTests : IAsyncLifetime
{
    private const int Cap = ContentTypeFieldLimit.Default;

    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public FieldCountCapTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static List<FieldDefinition> Fields(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new FieldDefinition { Name = $"Field{i}", DisplayName = $"Field {i}", Type = "string" })
            .ToList();

    private static string NewName() => "fieldcap-" + Guid.NewGuid().ToString("n")[..12];

    private async Task<string> StoredTypeAsync(int fieldCount)
    {
        var name = NewName();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = Fields(fieldCount),
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<ContentTypeDefinition?> ReadAsync(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name);
    }

    [Fact]
    public async Task A_create_with_one_field_more_than_the_cap_is_refused_and_nothing_is_stored()
    {
        var name = NewName();

        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = name,
            fields = Fields(Cap + 1),
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain($"at most {Cap} fields");
        (await ReadAsync(name)).Should().BeNull("a refused create must not leave the type behind");
    }

    [Fact]
    public async Task A_create_with_exactly_the_cap_is_accepted()
    {
        var name = NewName();

        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = name,
            fields = Fields(Cap),
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await ReadAsync(name))!.Fields.Should().HaveCount(Cap);
    }

    [Fact]
    public async Task Adding_a_field_to_a_type_already_at_the_cap_is_refused()
    {
        var name = await StoredTypeAsync(Cap);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{name}/fields", new
        {
            fieldName = "OneTooMany",
            type = "string",
        }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await ReadAsync(name))!.Fields.Should().HaveCount(Cap, "the refused field must not have been added");
    }

    [Fact]
    public async Task Adding_seo_fields_to_a_type_already_at_the_cap_is_refused()
    {
        var name = await StoredTypeAsync(Cap);

        var res = await _client.PostAsync($"/api/content-types/{name}/seo-fields", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await ReadAsync(name))!.Fields.Should().HaveCount(Cap, "no SEO field may have been added");
    }

    [Fact]
    public async Task A_type_stored_over_the_cap_can_still_be_changed_and_only_growth_is_refused()
    {
        var name = await StoredTypeAsync(Cap + 50);

        var update = await _client.PutAsJsonAsync($"/api/content-types/{name}/public-delivery",
            new { enabled = true }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        (await ReadAsync(name))!.IsPubliclyDeliverable.Should().BeTrue("a change that adds no field still applies");

        var grow = await _client.PostAsJsonAsync($"/api/content-types/{name}/fields", new
        {
            fieldName = "OneTooMany",
            type = "string",
        }, TestContext.Current.CancellationToken);
        grow.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadAsync(name))!.Fields.Should().HaveCount(Cap + 50, "the existing fields are left alone");
    }

    [Fact]
    public void The_cap_is_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ContentTypeFieldLimit.ConfigKey] = "3" })
            .Build();
        var validator = new ContentTypeValidatorService(configuration);

        validator.Validate("capped", "Capped", Fields(3)).IsValid.Should().BeTrue();

        var (isValid, errors) = validator.Validate("capped", "Capped", Fields(4));
        isValid.Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("at most 3 fields");
    }

    [Fact]
    public void An_oversized_field_list_gets_one_error_rather_than_one_per_field()
    {
        var validator = new ContentTypeValidatorService();
        var badNames = Enumerable.Range(1, Cap + 100)
            .Select(i => new FieldDefinition { Name = $"not_pascal_{i}", Type = "nope" })
            .ToList();

        var (isValid, errors) = validator.Validate("capped", "Capped", badNames);

        isValid.Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain($"at most {Cap} fields");
    }
}
