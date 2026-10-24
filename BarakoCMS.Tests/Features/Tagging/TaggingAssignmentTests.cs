using System.Net;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>Applying a term to a record, taking it off, and querying by term.</summary>
[Collection("Integration2")]
public class TaggingAssignmentTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingAssignmentTests(IntegrationTestFixture factory) => _factory = factory;

    private static Guid IdOf(System.Text.Json.JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task A_term_is_applied_to_a_record_listed_on_it_and_removed()
    {
        var api = await TaggingApi.SuperAdminAsync(_factory);
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
        var api = await TaggingApi.SuperAdminAsync(_factory);
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
        var api = await TaggingApi.SuperAdminAsync(_factory);
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
        var api = await TaggingApi.SuperAdminAsync(_factory);
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
        var api = await TaggingApi.SuperAdminAsync(_factory);

        var response = await api.ApplyAsync("missing-term-type", "1", Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_with_view_taxonomy_but_no_read_on_the_type_is_shown_no_record_ids()
    {
        var api = await TaggingApi.SuperAdminAsync(_factory);
        var termId = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        var hidden = Guid.NewGuid().ToString();
        var open = Guid.NewGuid().ToString();
        await api.ApplyAsync("hidden-type", hidden, termId);
        await api.ApplyAsync("open-type", open, termId);
        var viewer = await TaggingApi.ViewerAsync(_factory, ["open-type"]);

        var listed = await TaggingApi.ItemsAsync(await viewer.GetAsync($"/api/taxonomy/terms/{termId}/records"));
        listed.Should().HaveCount(1);
        listed[0].GetProperty("recordId").GetString().Should().Be(open);

        (await viewer.GetAsync($"/api/taxonomy/terms/{termId}/records?contentType=hidden-type"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/taxonomy/records/hidden-type/{hidden}/terms"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/taxonomy/records/open-type/{open}/terms"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_branch_scoped_caller_is_shown_no_record_from_another_branch()
    {
        var type = TaggingApi.Unique("ward");
        Guid mine = Guid.NewGuid(), other = Guid.NewGuid();
        Guid inMine = Guid.NewGuid(), inOther = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, BranchScoped = true,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            session.Store(new Content { Id = inMine, ContentType = type, BranchId = mine, Status = ContentStatus.Draft });
            session.Store(new Content { Id = inOther, ContentType = type, BranchId = other, Status = ContentStatus.Draft });
            await session.SaveChangesAsync();
        }

        var api = await TaggingApi.SuperAdminAsync(_factory);
        var termId = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        await api.ApplyAsync(type, inMine.ToString(), termId);
        await api.ApplyAsync(type, inOther.ToString(), termId);
        var viewer = await TaggingApi.ViewerAsync(_factory, [type], mine);

        var listed = await TaggingApi.ItemsAsync(await viewer.GetAsync($"/api/taxonomy/terms/{termId}/records"));

        listed.Should().HaveCount(1);
        listed[0].GetProperty("recordId").GetString().Should().Be(inMine.ToString());
        (await viewer.GetAsync($"/api/taxonomy/records/{type}/{inOther}/terms"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.GetAsync($"/api/taxonomy/records/{type}/{inMine}/terms"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_total_and_the_pages_count_only_the_records_the_caller_can_read()
    {
        var api = await TaggingApi.SuperAdminAsync(_factory);
        var termId = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        for (var i = 0; i < 3; i++) await api.ApplyAsync("open-page-type", Guid.NewGuid().ToString(), termId);
        for (var i = 0; i < 4; i++) await api.ApplyAsync("hidden-page-type", Guid.NewGuid().ToString(), termId);
        var viewer = await TaggingApi.ViewerAsync(_factory, ["open-page-type"]);

        var first = await TaggingApi.BodyAsync(await viewer.GetAsync($"/api/taxonomy/terms/{termId}/records?pageSize=2"));
        var second = await TaggingApi.BodyAsync(await viewer.GetAsync($"/api/taxonomy/terms/{termId}/records?pageSize=2&page=2"));

        first.GetProperty("totalItems").GetInt32().Should().Be(3);
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        second.GetProperty("items").GetArrayLength().Should().Be(1, "the unreadable records no longer take up room in the pages");
    }
}
