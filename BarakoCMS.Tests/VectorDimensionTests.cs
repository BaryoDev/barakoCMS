using System.Net;
using BarakoCMS.AI;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

public class VectorDimensionUnitTests
{
    [Fact]
    public void A_768_dimension_vector_compared_with_a_1024_dimension_vector_throws()
    {
        var stored = Enumerable.Repeat(1f, 768).ToArray();
        var query = Enumerable.Repeat(1f, 1024).ToArray();

        var act = () => Vectors.Cosine(query, stored);

        var ex = act.Should().Throw<VectorDimensionMismatchException>().Which;
        ex.Expected.Should().Be(1024);
        ex.Actual.Should().Be(768);
    }

    [Fact]
    public void Vectors_of_the_same_length_still_score()
    {
        Vectors.Cosine([1f, 0f], [1f, 0f]).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void An_empty_vector_still_scores_zero()
    {
        Vectors.Cosine([], [1f, 2f]).Should().Be(0);
    }
}

[Collection("Integration4")]
public class SemanticSearchDimensionTests(IntegrationTestFixture factory)
{
    [Fact]
    public async Task A_search_against_an_index_of_another_dimension_answers_409_with_a_clear_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "semdim-" + Guid.NewGuid().ToString("n")[..8];
        var id = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public }],
            });
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "Solar" },
            });
            session.Store(new ContentEmbedding
            {
                Id = id,
                ContentType = type,
                Slug = "solar",
                Title = "Solar",
                Vector = Enumerable.Repeat(1f, 768).ToArray(),
            });
            await session.SaveChangesAsync(ct);
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/public/{type}/semantic?q=photovoltaic+energy", ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("768").And.Contain("128").And.Contain("reindex");
    }
}
