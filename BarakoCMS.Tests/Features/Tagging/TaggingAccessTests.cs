using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>Who may call the Tagging endpoints, and that one tenant cannot see another's data.</summary>
[Collection("Integration1")]
public class TaggingAccessTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingAccessTests(IntegrationTestFixture factory) => _factory = factory;

    private static Guid IdOf(System.Text.Json.JsonElement e) => e.GetProperty("id").GetGuid();

    private static readonly Guid Any = Guid.NewGuid();

    /// <summary>Every endpoint of the module, each as a call to make.</summary>
    private static IEnumerable<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)> Calls() =>
    [
        ("create vocabulary", c => c.PostAsJsonAsync("/api/taxonomy/vocabularies", new { key = "denied", name = "Denied" })),
        ("list vocabularies", c => c.GetAsync("/api/taxonomy/vocabularies")),
        ("update vocabulary", c => c.PutAsJsonAsync($"/api/taxonomy/vocabularies/{Any}", new { name = "Denied" })),
        ("delete vocabulary", c => c.DeleteAsync($"/api/taxonomy/vocabularies/{Any}")),
        ("create term", c => c.PostAsJsonAsync($"/api/taxonomy/vocabularies/{Any}/terms", new { key = "denied", name = "Denied" })),
        ("list terms", c => c.GetAsync($"/api/taxonomy/vocabularies/{Any}/terms")),
        ("update term", c => c.PutAsJsonAsync($"/api/taxonomy/terms/{Any}", new { name = "Denied" })),
        ("delete term", c => c.DeleteAsync($"/api/taxonomy/terms/{Any}")),
        ("apply term", c => c.PostAsJsonAsync("/api/taxonomy/records/denied/1/terms", new { termId = Any })),
        ("remove term", c => c.DeleteAsync($"/api/taxonomy/records/denied/1/terms/{Any}")),
        ("terms on a record", c => c.GetAsync("/api/taxonomy/records/denied/1/terms")),
        ("records on a term", c => c.GetAsync($"/api/taxonomy/terms/{Any}/records")),
    ];

    [Fact]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_signed_in()
    {
        var anonymous = _factory.CreateClient();
        var calls = Calls().ToList();
        calls.Should().HaveCount(12);

        foreach (var (name, call) in calls)
        {
            (await call(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, name);
        }
    }

    [Fact]
    public async Task Every_endpoint_refuses_a_signed_in_user_who_holds_none_of_its_capabilities()
    {
        var user = await TaggingApi.RoleAsync(_factory, "User");
        var calls = Calls().ToList();
        calls.Should().HaveCount(12);

        foreach (var (name, call) in calls)
        {
            (await call(user)).StatusCode.Should().Be(HttpStatusCode.Forbidden, name);
        }
    }

    [Fact]
    public async Task The_seeded_admin_holds_all_three_capabilities()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var termId = IdOf(await api.CreateTermAsync(vocabularyId));

        (await api.ApplyAsync("admin-type", "1", termId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.Http.GetAsync("/api/taxonomy/vocabularies")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_tenant_sees_none_of_another_tenants_vocabularies_terms_or_assignments()
    {
        var a = await TaggingApi.TenantAsync(_factory, TaggingApi.Unique("tga"));
        var b = await TaggingApi.TenantAsync(_factory, TaggingApi.Unique("tgb"));
        var key = TaggingApi.Unique("shared");
        var vocabularyA = IdOf(await a.CreateVocabularyAsync(key));
        var termA = IdOf(await a.CreateTermAsync(vocabularyA));
        var recordId = Guid.NewGuid().ToString();
        (await a.ApplyAsync("tenant-type", recordId, termA)).StatusCode.Should().Be(HttpStatusCode.OK);

        var vocabulariesOfB = await TaggingApi.ItemsAsync(await b.Http.GetAsync("/api/taxonomy/vocabularies"));
        vocabulariesOfB.Select(IdOf).Should().NotContain(vocabularyA);

        (await b.Http.GetAsync($"/api/taxonomy/vocabularies/{vocabularyA}/terms")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.Http.GetAsync($"/api/taxonomy/terms/{termA}/records")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.ApplyAsync("tenant-type", recordId, termA)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.Http.DeleteAsync($"/api/taxonomy/vocabularies/{vocabularyA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.Http.PutAsJsonAsync($"/api/taxonomy/terms/{termA}", new { name = "Hijacked" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TaggingApi.ItemsAsync(await b.Http.GetAsync($"/api/taxonomy/records/tenant-type/{recordId}/terms"))).Should().BeEmpty();

        var stillThere = await TaggingApi.ItemsAsync(await a.Http.GetAsync($"/api/taxonomy/records/tenant-type/{recordId}/terms"));
        stillThere.Should().HaveCount(1, "tenant B's calls changed nothing in tenant A");

        var sameKeyInB = await b.CreateVocabularyAsync(key);
        IdOf(sameKeyInB).Should().Be(vocabularyA, "an id comes from the key, and each tenant has its own rows");
    }
}
