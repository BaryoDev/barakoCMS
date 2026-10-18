using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Deleting a saved query is recoverable until it is erased.
/// </summary>
[Collection("Sequential")]
public class QueryDeleteTests
{
    private readonly IntegrationTestFixture _factory;

    public QueryDeleteTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_deleted_query_disappears_from_reads_and_comes_back_on_restore()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var slug = await SaveAsync(client);

        (await client.DeleteAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/queries/{slug}/preview", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListSlugsAsync(client, "/api/queries")).Should().NotContain(slug);
        (await ListSlugsAsync(client, "/api/queries/deleted")).Should().Contain(slug);

        (await client.PostAsync($"/api/queries/{slug}/restore", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/queries/{slug}/preview", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListSlugsAsync(client, "/api/queries/deleted")).Should().NotContain(slug);
    }

    [Fact]
    public async Task Erasing_a_deleted_query_is_final()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var slug = await SaveAsync(client);
        await client.DeleteAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken);

        (await client.DeleteAsync($"/api/queries/{slug}/erase", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsync($"/api/queries/{slug}/restore", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<QueryDefinition>().AnyAsync(q => q.Slug == slug, TestContext.Current.CancellationToken))
            .Should().BeFalse("erase removes the row, it does not tombstone it");
    }

    [Fact]
    public async Task A_live_query_cannot_be_erased_in_one_call()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var slug = await SaveAsync(client);

        (await client.DeleteAsync($"/api/queries/{slug}/erase", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saving_over_a_deleted_slug_still_works_and_replaces_it()
    {
        var client = await ClientFor("SuperAdmin", "Admin");
        var slug = await SaveAsync(client, name: "Old");
        await client.DeleteAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken);

        await SaveAsync(client, slug: slug, name: "New");

        var got = await client.GetFromJsonAsync<JsonElement>($"/api/queries/{slug}", TestContext.Current.CancellationToken);
        got.GetProperty("name").GetString().Should().Be("New");
        (await ListSlugsAsync(client, "/api/queries/deleted")).Should().NotContain(slug);
        (await client.PostAsync($"/api/queries/{slug}/restore", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("POST", "restore")]
    [InlineData("DELETE", "erase")]
    public async Task Restore_and_erase_need_the_same_permission_as_delete(string method, string action)
    {
        var admin = await ClientFor("SuperAdmin", "Admin");
        var slug = await SaveAsync(admin);
        await admin.DeleteAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken);

        var editor = await ClientFor("Editor");
        (await editor.DeleteAsync($"/api/queries/{slug}", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var res = await editor.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), $"/api/queries/{slug}/{action}"),
            TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = _factory.CreateClient();
        (await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/queries/{slug}/{action}"),
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpClient> ClientFor(params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync(roles));
        return client;
    }

    private static async Task<List<string>> ListSlugsAsync(HttpClient client, string url)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(url + "?pageSize=100", TestContext.Current.CancellationToken);
        return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToList();
    }

    private async Task<string> SaveAsync(HttpClient client, string? slug = null, string name = "Subscribers")
    {
        slug ??= "q" + Guid.NewGuid().ToString("n")[..10];
        var type = "sub" + Guid.NewGuid().ToString("n")[..10];

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Subscriber",
                Fields = [new FieldDefinition { Name = "Email", Type = "email" }],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var res = await client.PostAsJsonAsync("/api/queries", new
        {
            name,
            slug,
            contentType = type,
            filters = Array.Empty<object>(),
            fields = new[] { "Email" },
            limit = 10,
        }, TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return slug;
    }
}
