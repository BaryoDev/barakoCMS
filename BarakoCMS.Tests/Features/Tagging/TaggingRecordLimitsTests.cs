using System.Net;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Tagging;

/// <summary>How many terms a record carries, and what happens to them when its entry is erased.</summary>
[Collection("Integration4")]
public class TaggingRecordLimitsTests
{
    private readonly IntegrationTestFixture _factory;

    public TaggingRecordLimitsTests(IntegrationTestFixture factory) => _factory = factory;

    private static Guid IdOf(System.Text.Json.JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task A_record_carries_at_most_one_hundred_terms()
    {
        var api = await TaggingApi.SuperAdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var recordId = Guid.NewGuid().ToString();
        var first = Guid.Empty;

        for (var i = 0; i < 100; i++)
        {
            var term = IdOf(await api.CreateTermAsync(vocabularyId, $"limit-{i}"));
            if (i == 0) first = term;
            (await api.ApplyAsync("limit-type", recordId, term)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var one = IdOf(await api.CreateTermAsync(vocabularyId, "limit-over"));
        (await api.ApplyAsync("limit-type", recordId, one)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.ApplyAsync("limit-type", recordId, first)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a term the record already carries is not a new one");
        (await api.ApplyAsync("limit-type", Guid.NewGuid().ToString(), one)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the limit is per record");
    }

    [Fact]
    public async Task A_term_with_more_records_than_the_scan_cap_says_the_list_is_truncated()
    {
        var api = await TaggingApi.SuperAdminAsync(_factory);
        var vocabularyId = IdOf(await api.CreateVocabularyAsync());
        var termId = IdOf(await api.CreateTermAsync(vocabularyId, "cap-term"));
        var smallId = IdOf(await api.CreateTermAsync(vocabularyId, "cap-small"));
        var type = TaggingApi.Unique("cap");
        var now = DateTimeOffset.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(Enumerable.Range(0, 5001).Select(i => new BarakoCMS.Tagging.TermAssignment
            {
                Id = Guid.NewGuid(),
                TermId = termId,
                VocabularyId = vocabularyId,
                ContentType = type,
                RecordId = Guid.NewGuid().ToString(),
                AssignedAt = now.AddSeconds(-i),
            }).ToArray());
            session.Store(new BarakoCMS.Tagging.TermAssignment
            {
                Id = Guid.NewGuid(), TermId = smallId, VocabularyId = vocabularyId,
                ContentType = type, RecordId = Guid.NewGuid().ToString(), AssignedAt = now,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var big = await TaggingApi.BodyAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{termId}/records"));
        var small = await TaggingApi.BodyAsync(await api.Http.GetAsync($"/api/taxonomy/terms/{smallId}/records"));

        big.GetProperty("truncated").GetBoolean().Should().BeTrue();
        big.GetProperty("totalItems").GetInt32().Should().Be(5000);
        small.GetProperty("truncated").GetBoolean().Should().BeFalse();
        small.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Erasing_a_content_entry_removes_the_terms_on_it_and_leaves_other_records_alone()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
            await writer.CreateAsync(new barakoCMS.Events.ContentCreated(
                id, "tagged-probe", new Dictionary<string, object> { ["FullName"] = "x" },
                ContentStatus.Draft, Guid.NewGuid(), "x", SensitivityLevel.Public), default);
            await session.SaveChangesAsync();
        }

        var api = await TaggingApi.SuperAdminAsync(_factory);
        var term = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        (await api.ApplyAsync("tagged-probe", id.ToString(), term)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.ApplyAsync("tagged-probe", other.ToString(), term)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(term)).Should().Be(2);

        (await api.Http.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAsync(term)).Should().Be(1, "only the erased entry's assignment goes");
    }

    [Fact]
    public async Task A_record_tagged_by_its_uppercase_id_loses_its_terms_when_the_entry_is_erased()
    {
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
            await writer.CreateAsync(new barakoCMS.Events.ContentCreated(
                id, "tagged-probe", new Dictionary<string, object> { ["FullName"] = "x" },
                ContentStatus.Draft, Guid.NewGuid(), "x", SensitivityLevel.Public), default);
            await session.SaveChangesAsync();
        }

        var api = await TaggingApi.SuperAdminAsync(_factory);
        var term = IdOf(await api.CreateTermAsync(IdOf(await api.CreateVocabularyAsync())));
        (await api.ApplyAsync("Tagged-Probe", id.ToString().ToUpperInvariant(), term)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.ApplyAsync("tagged-probe", $"{{{id}}}", term)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(term)).Should().Be(1, "both spellings name one record");

        (await api.Http.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAsync(term)).Should().Be(0);
    }

    private async Task<int> CountAsync(Guid termId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession("*DEFAULT*");
        return await session.Query<BarakoCMS.Tagging.TermAssignment>().CountAsync(a => a.TermId == termId);
    }
}
