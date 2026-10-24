using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>/api/taxonomy/vocabularies/{id}/terms and /api/taxonomy/terms/{id}.</summary>
[Collection("Integration2")]
public class TaggingTermTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingTermTests(IntegrationTestFixture factory) => _factory = factory;

    private static Guid IdOf(System.Text.Json.JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Terms_nest_and_the_list_can_be_limited_to_the_children_of_one_term()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var animals = IdOf(await api.CreateTermAsync(vocabularyId, "animals"));
        var cats = IdOf(await api.CreateTermAsync(vocabularyId, "cats", animals));
        await api.CreateTermAsync(vocabularyId, "plants");

        var all = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/vocabularies/{vocabularyId}/terms"));
        all.Should().HaveCount(3);

        var children = await TaggingApi.ItemsAsync(
            await api.Http.GetAsync($"/api/taxonomy/vocabularies/{vocabularyId}/terms?parentId={animals}"));
        children.Should().HaveCount(1);
        IdOf(children[0]).Should().Be(cats);
        children[0].GetProperty("parentId").GetGuid().Should().Be(animals);
    }

    [Fact]
    public async Task A_term_key_is_unique_within_its_vocabulary_but_may_repeat_in_another()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var first = IdOf(await api.CreateVocabularyAsync());
        var second = IdOf(await api.CreateVocabularyAsync());
        await api.CreateTermAsync(first, "red");

        var again = await api.Http.PostAsJsonAsync($"/api/taxonomy/vocabularies/{first}/terms", new { key = "red", name = "Red" });
        var elsewhere = await api.Http.PostAsJsonAsync($"/api/taxonomy/vocabularies/{second}/terms", new { key = "red", name = "Red" });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_parent_from_another_vocabulary_is_400()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var first = IdOf(await api.CreateVocabularyAsync());
        var second = IdOf(await api.CreateVocabularyAsync());
        var foreign = IdOf(await api.CreateTermAsync(second));

        var response = await api.Http.PostAsJsonAsync($"/api/taxonomy/vocabularies/{first}/terms",
            new { key = "child", name = "Child", parentId = foreign });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_term_cannot_move_under_itself_or_under_its_own_descendant()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var top = IdOf(await api.CreateTermAsync(vocabularyId));
        var middle = IdOf(await api.CreateTermAsync(vocabularyId, parentId: top));
        var bottom = IdOf(await api.CreateTermAsync(vocabularyId, parentId: middle));

        var underItself = await api.Http.PutAsJsonAsync($"/api/taxonomy/terms/{top}", new { name = "Top", parentId = top });
        var underBottom = await api.Http.PutAsJsonAsync($"/api/taxonomy/terms/{top}", new { name = "Top", parentId = bottom });

        underItself.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        underBottom.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var moved = await api.Http.PutAsJsonAsync($"/api/taxonomy/terms/{bottom}", new { name = "Bottom", parentId = top });
        moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        (await TaggingApi.BodyAsync(moved)).GetProperty("parentId").GetGuid().Should().Be(top);
    }

    [Fact]
    public async Task Terms_cannot_nest_deeper_than_eight_levels()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        Guid? parent = null;
        for (var level = 1; level <= 8; level++)
        {
            parent = IdOf(await api.CreateTermAsync(vocabularyId, parentId: parent));
        }

        var ninth = await api.Http.PostAsJsonAsync($"/api/taxonomy/vocabularies/{vocabularyId}/terms",
            new { key = "too-deep", name = "Too deep", parentId = parent });

        ninth.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_term_with_children_cannot_be_deleted_until_they_are_gone()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var parent = IdOf(await api.CreateTermAsync(vocabularyId));
        var child = IdOf(await api.CreateTermAsync(vocabularyId, parentId: parent));

        (await api.Http.DeleteAsync($"/api/taxonomy/terms/{parent}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await api.Http.DeleteAsync($"/api/taxonomy/terms/{child}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.Http.DeleteAsync($"/api/taxonomy/terms/{parent}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deleting_a_term_takes_it_off_the_records_that_carried_it()
    {
        var api = await TaggingApi.AdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var kept = IdOf(await api.CreateTermAsync(vocabularyId));
        var removed = IdOf(await api.CreateTermAsync(vocabularyId));
        var recordId = Guid.NewGuid().ToString();
        await api.ApplyAsync("term-delete-type", recordId, kept);
        await api.ApplyAsync("term-delete-type", recordId, removed);

        (await api.Http.DeleteAsync($"/api/taxonomy/terms/{removed}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var terms = await TaggingApi.ItemsAsync(await api.Http.GetAsync($"/api/taxonomy/records/term-delete-type/{recordId}/terms"));
        terms.Should().HaveCount(1);
        IdOf(terms[0]).Should().Be(kept);
    }
}
