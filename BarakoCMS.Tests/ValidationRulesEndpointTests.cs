using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The rules over HTTP, so the shapes a rule takes on the way through a request body and back out
/// of the database are the ones covered.
/// </summary>
[Collection("Sequential")]
public class ValidationRulesEndpointTests
{
    private readonly IntegrationTestFixture _fixture;

    public ValidationRulesEndpointTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private static Task<HttpResponseMessage> SaveTypeAsync(HttpClient client, string name, object rules) =>
        client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = name,
            fields = new object[]
            {
                new { name = "Student", type = "string" },
                new { name = "Grade", type = "int", validationRules = rules },
            },
        }, Ct);

    private static async Task<string> GradedTypeAsync(HttpClient client)
    {
        var asked = $"grades{Guid.NewGuid():n}"[..16];

        var res = await SaveTypeAsync(client, asked, new { min = 0, max = 100 });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode,
            await res.Content.ReadAsStringAsync(Ct));

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<HttpResponseMessage> CreateEntryAsync(HttpClient client, string type, int grade) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Student"] = "Ana", ["Grade"] = grade },
        }, Ct);

    [Fact]
    public async Task An_entry_above_a_fields_max_is_refused_on_create()
    {
        var client = await AdminAsync();
        var type = await GradedTypeAsync(client);

        var res = await CreateEntryAsync(client, type, 101);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Grade").And.Contain("max");
    }

    [Fact]
    public async Task An_entry_inside_the_bounds_is_accepted()
    {
        var client = await AdminAsync();
        var type = await GradedTypeAsync(client);

        var res = await CreateEntryAsync(client, type, 100);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_entry_edited_above_a_fields_max_is_refused_on_update()
    {
        var client = await AdminAsync();
        var type = await GradedTypeAsync(client);

        var created = await CreateEntryAsync(client, type, 90);
        created.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", await created.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = body.RootElement.GetProperty("id").GetGuid();

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Student"] = "Ana", ["Grade"] = 250 },
        }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Grade").And.Contain("max");
    }

    [Fact]
    public async Task A_type_stored_with_an_unknown_rule_still_accepts_a_new_field()
    {
        var type = $"legacy{Guid.NewGuid():n}"[..16];
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition
                    {
                        Name = "Code",
                        DisplayName = "Code",
                        Type = "string",
                        ValidationRules = new() { ["matches"] = "^[A-Z]+$" },
                    },
                ],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Grade",
            type = "int",
            validationRules = new { max = 100 },
        }, Ct);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode,
            await res.Content.ReadAsStringAsync(Ct));

        var refused = await client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Score",
            type = "int",
            validationRules = new { maximum = 100 },
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the field being added is still checked");
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Score").And.Contain("maximum");
    }

    [Fact]
    public async Task A_type_saved_with_an_unknown_rule_name_is_refused()
    {
        var client = await AdminAsync();

        var res = await SaveTypeAsync(client, $"grades{Guid.NewGuid():n}"[..16], new { maximum = 100 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Grade").And.Contain("maximum");
    }

    [Fact]
    public async Task A_type_saved_with_a_bound_that_is_not_a_number_is_refused()
    {
        var client = await AdminAsync();

        var res = await SaveTypeAsync(client, $"grades{Guid.NewGuid():n}"[..16], new { max = "a lot" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Grade").And.Contain("max");
    }
}
