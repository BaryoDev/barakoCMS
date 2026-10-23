using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>Clients and calls the Tagging tests share.</summary>
internal sealed class TaggingApi(IntegrationTestFixture factory, HttpClient http)
{
    public HttpClient Http => http;

    public static async Task<TaggingApi> AdminAsync(IntegrationTestFixture factory) =>
        new(factory, WithToken(factory, await factory.StoredUserTokenAsync("Admin")));

    public static async Task<HttpClient> RoleAsync(IntegrationTestFixture factory, string role) =>
        WithToken(factory, await factory.StoredUserTokenAsync(role));

    /// <summary>A SuperAdmin whose only membership is <paramref name="slug"/>, calling as that tenant.</summary>
    public static async Task<TaggingApi> TenantAsync(IntegrationTestFixture factory, string slug)
    {
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            session.Store(new User
            {
                Id = userId,
                Username = $"tg-{Guid.NewGuid():n}"[..14],
                Email = $"tg-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return new TaggingApi(factory, client);
    }

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private static HttpClient WithToken(IntegrationTestFixture factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<JsonElement> CreateVocabularyAsync(string? key = null)
    {
        var response = await http.PostAsJsonAsync("/api/taxonomy/vocabularies",
            new { key = key ?? Unique("voc"), name = "A vocabulary" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    public async Task<JsonElement> CreateTermAsync(Guid vocabularyId, string? key = null, Guid? parentId = null)
    {
        var response = await http.PostAsJsonAsync($"/api/taxonomy/vocabularies/{vocabularyId}/terms",
            new { key = key ?? Unique("term"), name = "A term", parentId });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    public Task<HttpResponseMessage> ApplyAsync(string contentType, string recordId, Guid termId) =>
        http.PostAsJsonAsync($"/api/taxonomy/records/{contentType}/{recordId}/terms", new { termId });

    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task<List<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        return body.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
