using System.Net;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>Applying a term to a record, taking it off, and querying by term.</summary>
[Collection("Sequential")]
public class TaggingAssignmentTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingAssignmentTests(IntegrationTestFixture factory) => _factory = factory;

    private static Guid IdOf(System.Text.Json.JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task A_term_is_applied_to_a_record_listed_on_it_and_removed()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var termId = IdOf(await api.CreateTermAsync(vocabularyId));
        var recordId = Guid.NewGuid().ToString();

        var applied = await api.ApplyAsync("applied-type", recordId, termId);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());

        var onRecord = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/records/applied-type/{recordId}/terms"));
        onRecord.Should().HaveCount(1);
        IdOf(onRecord[0]).Should().Be(termId);

        (await api.Http.DeleteAsync($"/api/taxonomy/records/applied-type/{recordId}/terms/{termId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/records/applied-type/{recordId}/terms")))
            .Should().BeEmpty();
        (await api.Http.DeleteAsync($"/api/taxonomy/records/applied-type/{recordId}/terms/{termId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Applying_the_same_term_twice_is_one_assignment()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var termId = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        var recordId = Guid.NewGuid().ToString();

        var first = await TaggingApi.BodyAsync(await api.ApplyAsync("twice-type", recordId, termId));
        var second = await api.ApplyAsync("twice-type", recordId, termId);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await TaggingApi.BodyAsync(second)).GetProperty("assignedAt").GetDateTimeOffset()
            .Should().Be(first.GetProperty("assignedAt").GetDateTimeOffset(), "the second call changes nothing");
        var records = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{termId}/records"));
        records.Should().HaveCount(1);
    }

    [Fact]
    public async Task Two_record_types_share_one_term_and_a_query_by_the_term_finds_both()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var termId = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync()), "priority"));
        var contactId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var otherTerm = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        var untagged = Guid.NewGuid().ToString();
        await api.ApplyAsync("contact", contactId, termId);
        await api.ApplyAsync("file", fileId, termId);
        await api.ApplyAsync("file", untagged, otherTerm);

        var records = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{termId}/records"));

        records.Should().HaveCount(2);
        records.Select(r => (r.GetProperty("contentType").GetString(), r.GetProperty("recordId").GetString()))
            .Should().BeEquivalentTo([("contact", contactId), ("file", fileId)]);

        var onlyFiles = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{termId}/records?contentType=file"));
        onlyFiles.Should().HaveCount(1);
        onlyFiles[0].GetProperty("recordId").GetString().Should().Be(fileId);
    }

    [Fact]
    public async Task A_query_can_include_the_records_tagged_with_a_descendant_term()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var animals = IdOf(await api.CreateTermAsync(vocabularyId));
        var cats = IdOf(await api.CreateTermAsync(vocabularyId, parentId: animals));
        var onAnimal = Guid.NewGuid().ToString();
        var onCat = Guid.NewGuid().ToString();
        await api.ApplyAsync("descend-type", onAnimal, animals);
        await api.ApplyAsync("descend-type", onCat, cats);

        var direct = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{animals}/records"));
        var withDescendants = await TaggingApi.ItemsAsync(
            await api.Http.GetAsync($"/api/taxonomy/terms/{animals}/records?includeDescendants=true"));

        direct.Should().HaveCount(1);
        direct[0].GetProperty("recordId").GetString().Should().Be(onAnimal);
        withDescendants.Should().HaveCount(2);
        withDescendants.Select(r => r.GetProperty("recordId").GetString()).Should().BeEquivalentTo([onAnimal, onCat]);
    }

    [Fact]
    public async Task Applying_a_term_that_does_not_exist_is_404()
    {
        var api = await TaggingApi.AdminAsync(_factory);

        var response = await api.ApplyAsync("missing-term-type", "1", Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
