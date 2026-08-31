using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Records every statement Marten executes, so a test can assert what Postgres was actually asked.
/// </summary>
internal sealed class CapturingMartenLogger : IMartenLogger, IMartenSessionLogger
{
    public ConcurrentQueue<(string Sql, string[] Parameters)> Commands { get; } = new();

    public IMartenSessionLogger StartSession(IQuerySession session) => this;

    public void SchemaChange(string sql) { }

    public void LogSuccess(NpgsqlCommand command) => Record(command);

    public void LogFailure(NpgsqlCommand command, Exception ex) => Record(command);

    public void LogSuccess(NpgsqlBatch batch)
    {
        foreach (var command in batch.BatchCommands)
        {
            Commands.Enqueue((command.CommandText, Values(command.Parameters)));
        }
    }

    public void LogFailure(NpgsqlBatch batch, Exception ex) => LogSuccess(batch);

    public void LogFailure(Exception ex, string message) { }

    public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }

    public void OnBeforeExecute(NpgsqlCommand command) { }

    public void OnBeforeExecute(NpgsqlBatch batch) { }

    private void Record(NpgsqlCommand command) =>
        Commands.Enqueue((command.CommandText, Values(command.Parameters)));

    private static string[] Values(System.Collections.IEnumerable parameters)
    {
        var values = new List<string>();
        foreach (NpgsqlParameter parameter in parameters)
        {
            values.Add(parameter.Value?.ToString() ?? string.Empty);
        }

        return values.ToArray();
    }
}

/// <summary>
/// The anonymous slug route must ask Postgres for the one entry, not for all of them.
/// </summary>
/// <remarks>
/// The non-preview path queried every published, Public entry of the type with <c>ToListAsync</c>
/// and then ran <c>FirstOrDefault</c> on the slug in memory. On a blog with 20k posts every
/// uncached request deserialized 20k documents to return one, and a 404 probe cost exactly the
/// same. The 60-second cache header only helps behind a CDN.
///
/// A behavioural assertion cannot see this: both shapes return the same entry. What separates them
/// is whether the slug ever reaches the database, so that is what is asserted, against the
/// statements Postgres was actually sent. Correctness of the match is asserted alongside it, since
/// pushing a predicate down is only worth anything if it still finds the right row.
/// </remarks>
[Collection("Sequential")]
public class SlugLookupPushdownTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly CapturingMartenLogger _logger = new();
    private readonly WebApplicationFactoryLike _host;

    /// <summary>Keeps the derived host and its client together for the life of the test class.</summary>
    internal sealed record WebApplicationFactoryLike(HttpClient Client, IServiceProvider Services);

    public SlugLookupPushdownTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureMarten(options => options.Logger(_logger))));
        _host = new WebApplicationFactoryLike(derived.CreateClient(), derived.Services);
    }

    private async Task<(string Type, string Slug)> SeedAsync(int howMany)
    {
        var typeName = "slugpush-" + Guid.NewGuid().ToString("n")[..8];

        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = typeName,
            DisplayName = "Slug Pushdown",
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
                new FieldDefinition { Name = "Title", Type = "Text", Sensitivity = SensitivityLevel.Public },
            ],
        });

        string? wanted = null;
        for (var i = 0; i < howMany; i++)
        {
            var slug = $"post-{Guid.NewGuid():N}";
            wanted ??= slug;
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = typeName,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["slug"] = slug, ["Title"] = $"Post {i}" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (typeName, wanted!);
    }

    [Fact]
    public async Task The_slug_is_matched_by_postgres_not_in_memory()
    {
        var (type, slug) = await SeedAsync(5);

        // Seeding wrote the slug into a document body, so only what happens from here counts.
        _logger.Commands.Clear();

        var response = await _host.Client.GetAsync($"/api/public/{type}/{slug}", TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        _logger.Commands.Should().Contain(c => c.Parameters.Contains(slug),
            "the slug has to reach the database, or the endpoint is loading every published entry of "
            + "the type and matching in memory");
    }

    [Fact]
    public async Task A_404_probe_does_not_load_the_whole_type()
    {
        var (type, _) = await SeedAsync(5);
        var missing = $"no-such-{Guid.NewGuid():N}";

        _logger.Commands.Clear();

        var response = await _host.Client.GetAsync($"/api/public/{type}/{missing}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _logger.Commands.Should().Contain(c => c.Parameters.Contains(missing),
            "a miss cost the same full-table read as a hit, so 404 probing was as expensive as serving");
    }

    /// <summary>The pushed-down predicate still finds the right entry, and only that entry.</summary>
    [Fact]
    public async Task The_matching_entry_is_returned()
    {
        var (type, slug) = await SeedAsync(5);

        var response = await _host.Client.GetAsync($"/api/public/{type}/{slug}", TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("slug").GetString().Should().Be(slug);
    }

    /// <summary>
    /// The in-memory match used OrdinalIgnoreCase, so the SQL one has to be case-insensitive too or
    /// this is a behaviour change dressed as an optimisation.
    /// </summary>
    [Fact]
    public async Task The_match_is_still_case_insensitive()
    {
        var (type, slug) = await SeedAsync(3);

        var response = await _host.Client.GetAsync($"/api/public/{type}/{slug.ToUpperInvariant()}", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("slug").GetString().Should().Be(slug);
    }

    /// <summary>
    /// Underscores and percent signs are ordinary slug characters. Reaching for ILIKE to get
    /// case-insensitivity would make them wildcards and answer with the wrong entry.
    /// </summary>
    [Fact]
    public async Task A_slug_holding_an_underscore_is_not_treated_as_a_wildcard()
    {
        var typeName = "slugpush-" + Guid.NewGuid().ToString("n")[..8];
        var stem = Guid.NewGuid().ToString("n")[..8];

        using (var scope = _host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = typeName,
                DisplayName = "Slug Pushdown",
                IsPubliclyDeliverable = true,
                Fields =
                [
                    new FieldDefinition { Name = "slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
                ],
            });

            // Only the "x" variant exists. Under ILIKE, asking for "a_b" would match it.
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = typeName,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["slug"] = $"{stem}axb" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await _host.Client.GetAsync($"/api/public/{typeName}/{stem}a_b", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "_ is a character in a slug, not a single-character wildcard");
    }

    /// <summary>A draft with the requested slug must still be a 404, filter or no filter.</summary>
    [Fact]
    public async Task An_unpublished_entry_is_still_invisible()
    {
        var typeName = "slugpush-" + Guid.NewGuid().ToString("n")[..8];
        var slug = $"draft-{Guid.NewGuid():N}";

        using (var scope = _host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = typeName,
                DisplayName = "Slug Pushdown",
                IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "slug", Type = "slug", Sensitivity = SensitivityLevel.Public }],
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = typeName,
                Status = ContentStatus.Draft,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["slug"] = slug },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await _host.Client.GetAsync($"/api/public/{typeName}/{slug}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
