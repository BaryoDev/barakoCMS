using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>/api/taxonomy/vocabularies.</summary>
[Collection("Sequential")]
public class TaggingVocabularyTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingVocabularyTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_vocabulary_is_created_listed_renamed_and_deleted()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var key = TaggingApi.Unique("voc");

        var created = await api.CreateVocabularyAsync(key);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("key").GetString().Should().Be(key);

        var listed = await TaggingApi.ItemsAsync(await api.Http.GetAsync("/api/taxonomy/vocabularies?pageSize=100"));
        listed.Should().NotBeEmpty();
        listed.Select(v => v.GetProperty("id").GetGuid()).Should().Contain(id);

        var renamed = await api.Http.PutAsJsonAsync($"/api/taxonomy/vocabularies/{id}",
            new { name = "Renamed", description = "About it" });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        var body = await TaggingApi.BodyAsync(renamed);
        body.GetProperty("name").GetString().Should().Be("Renamed");
        body.GetProperty("key").GetString().Should().Be(key, "the key is the vocabulary's identity and does not change");

        (await api.Http.DeleteAsync($"/api/taxonomy/vocabularies/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.Http.DeleteAsync($"/api/taxonomy/vocabularies/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_second_vocabulary_with_the_same_key_is_409()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var key = TaggingApi.Unique("dup");
        await api.CreateVocabularyAsync(key);

        var again = await api.Http.PostAsJsonAsync("/api/taxonomy/vocabularies", new { key, name = "Again" });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("")]
    public async Task A_key_that_is_not_a_slug_is_400(string key)
    {
        var api = await TaggingApi.AdminAsync(_factory);

        var response = await api.Http.PostAsJsonAsync("/api/taxonomy/vocabularies", new { key, name = "Bad key" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_vocabulary_removes_its_terms_and_their_assignments()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabulary = await api.CreateVocabularyAsync();
        var vocabularyId = vocabulary.GetProperty("id").GetGuid();
        var term = await api.CreateTermAsync(vocabularyId);
        var termId = term.GetProperty("id").GetGuid();
        var recordId = Guid.NewGuid().ToString();
        (await api.ApplyAsync("deleted-voc-type", recordId, termId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.Http.DeleteAsync($"/api/taxonomy/vocabularies/{vocabularyId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await api.Http.GetAsync($"/api/taxonomy/vocabularies/{vocabularyId}/terms")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.Http.GetAsync($"/api/taxonomy/terms/{termId}/records")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var onRecord = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/records/deleted-voc-type/{recordId}/terms"));
        onRecord.Should().BeEmpty("the assignment went with the vocabulary");
    }

    [Fact]
    public async Task The_list_is_a_page_and_a_page_size_above_the_cap_is_clamped()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        await api.CreateVocabularyAsync();

        var response = await api.Http.GetAsync("/api/taxonomy/vocabularies?pageSize=100000");

        var body = await TaggingApi.BodyAsync(response);
        body.GetProperty("pageSize").GetInt32().Should().Be(100);
    }
}
