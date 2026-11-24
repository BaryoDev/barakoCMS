using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A slug value stored before the slug check refused a trailing line feed still belongs to its
/// entry: an edit of another field that sends it back unchanged is accepted, and a new value is
/// checked.
/// </summary>
[Collection("Integration1")]
public class StoredSlugValueTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public StoredSlugValueTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AnUnchangedStoredSlugDoesNotBlockAnEditOfAnotherField()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "storedslug" + Guid.NewGuid().ToString("n")[..10];
        var defined = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = type,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Handle", type = "slug" } },
        }, ct);
        defined.StatusCode.Should().Be(HttpStatusCode.OK, await defined.Content.ReadAsStringAsync(ct));

        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "old", ["Handle"] = "my-slug" },
        }, ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        // The value an earlier version accepted, written underneath the API.
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var entry = await session.LoadAsync<Content>(id, ct);
            entry!.Data["Handle"] = "my-slug\n";
            session.Store(entry);
            await session.SaveChangesAsync(ct);
        }

        var kept = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Handle"] = "my-slug\n" },
            version = 1,
        }, ct);
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(ct));

        var changed = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Handle"] = "other-slug\n" },
            version = 2,
        }, ct);
        changed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await changed.Content.ReadAsStringAsync(ct));
    }
}
