using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BarakoCMS.AI;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The anonymous semantic search reads a bounded number of embeddings, says when it stopped early,
/// and verifies its hits with one query. See #620.
/// </summary>
/// <remarks>
/// A behavioural assertion cannot see an unbounded read: both shapes return the same hits on a small
/// type. So the embeddings statement Postgres was sent is captured and run again, and the rows it
/// returns are counted.
/// </remarks>
[Collection("Sequential")]
public class SemanticSearchScanBoundTests
{
    private const int ScanLimit = 5;
    private const string Words = "photovoltaic renewable sunlight energy generation";

    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public SemanticSearchScanBoundTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.ConfigureMarten(options => options.Logger(_logger));
                services.PostConfigure<AiOptions>(o => o.SemanticSearchScanLimit = ScanLimit);
            }));
        _client = derived.CreateClient();
        _services = derived.Services;
    }

    private async Task<string> SeedAsync(int entries)
    {
        var type = "semscan-" + Guid.NewGuid().ToString("n")[..8];
        var vector = await new FakeEmbeddingClient().EmbedAsync(Words, TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields = [new FieldDefinition { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public }],
        });

        for (var i = 0; i < entries; i++)
        {
            var id = Guid.NewGuid();
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = $"Solar {i}" },
            });
            session.Store(new ContentEmbedding
            {
                Id = id,
                ContentType = type,
                Slug = $"solar-{i}",
                Title = $"Solar {i}",
                Vector = vector!,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private async Task<JsonElement> SearchAsync(string type, int limit = 10)
    {
        var response = await _client.GetAsync(
            $"/api/public/{type}/semantic?q={Uri.EscapeDataString(Words)}&limit={limit}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private List<(string Sql, NpgsqlParameter[] Parameters)> StatementsAgainst(string table) =>
        _logger.RawCommands
            .Where(c => Regex.IsMatch(c.Sql, $@"\b{table}\b", RegexOptions.IgnoreCase))
            .ToList();

    [Fact]
    public async Task The_embeddings_query_reads_no_more_than_the_scan_limit_plus_one_row()
    {
        const int indexed = 12;
        var type = await SeedAsync(indexed);
        _logger.RawCommands.Clear();

        await SearchAsync(type);

        var reads = StatementsAgainst("mt_doc_content_embeddings");
        reads.Should().HaveCount(1, "one statement reads the embeddings");

        var (sql, parameters) = reads[0];
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"select count(*) from ({sql.TrimEnd().TrimEnd(';')}) as scanned", connection);
        foreach (var p in parameters) command.Parameters.Add(p);
        var rows = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        rows.Should().BeGreaterThan(0, "the statement has to read something for the bound to mean anything");
        rows.Should().BeLessThanOrEqualTo(ScanLimit + 1,
            $"{indexed} embeddings are indexed and an anonymous request must not read them all");
    }

    [Fact]
    public async Task A_scan_that_hits_the_limit_is_reported_as_truncated()
    {
        var type = await SeedAsync(ScanLimit + 3);

        var body = await SearchAsync(type);

        body.GetProperty("results").GetArrayLength().Should().BeGreaterThan(0);
        body.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(ScanLimit)]
    [InlineData(ScanLimit - 2)]
    public async Task A_scan_that_reads_every_embedding_is_not_reported_as_truncated(int indexed)
    {
        var type = await SeedAsync(indexed);

        var body = await SearchAsync(type);

        body.GetProperty("results").GetArrayLength().Should().Be(indexed);
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_hits_are_verified_with_one_query()
    {
        var type = await SeedAsync(4);
        _logger.RawCommands.Clear();

        var body = await SearchAsync(type);

        body.GetProperty("results").GetArrayLength().Should().Be(4);
        StatementsAgainst("mt_doc_contents").Should().HaveCount(1,
            "four hits are re-checked for freshness, and that should be one round trip, not four");
    }
}
