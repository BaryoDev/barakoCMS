using FluentAssertions;
using barakoCMS.Models;
using Marten;
using barakoCMS.Data;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class DataSeederTests
{
    private readonly IntegrationTestFixture _factory;

    public DataSeederTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    // Asserts on this document rather than on the run's total. The fixture database is shared, so
    // any other test that left a Content with a null SearchText is picked up by the same backfill
    // and counted. Reading the console total made this test depend on what else had already run:
    // in a full-suite run it saw 8 rather than 0 and failed for a reason that had nothing to do
    // with the behaviour under test.
    [Fact]
    public async Task BackfillSearchText_LeavesContentWithNoDefinitionUnindexed()
    {
        var type = $"missing_definition_{Guid.NewGuid():N}";
        var id = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new()
            {
                ["Title"] = "No definition"
            },
            SearchText = null
        });

        await session.SaveChangesAsync();

        await DataSeeder.BackfillSearchTextAsync(session);

        var after = await session.LoadAsync<Content>(id);

        after.Should().NotBeNull();
        after!.SearchText
            .Should()
            .BeNull("a content type with no definition has no public fields to index, so the "
                  + "backfill must leave the document alone rather than write an empty string");
    }

    [Fact]
    public async Task BackfillSearchText_SecondRun_ReportsZeroUpdates()
    {
        var type = $"backfill_once_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = new()
        {
            new()
            {
                Name = "Title",
                DisplayName = "Title",
                Type = "string",
                Sensitivity = SensitivityLevel.Public
            }
        }
        });

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = "Original title" },
            SearchText = null
        };

        session.Store(content);
        await session.SaveChangesAsync();

        // First run (updates document)
        await DataSeeder.BackfillSearchTextAsync(session);

        // Second run (should report 0)
        var output = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(output);
            await DataSeeder.BackfillSearchTextAsync(session);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        output.ToString()
            .Should()
            .Contain("Backfilled SearchText for 0 content documents.");
    }
    [Fact]
    public async Task BackfillSearchText_PagesThroughAllContent()
    {
        var type = $"backfill_paging_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = new()
        {
            new()
            {
                Name = "Title",
                DisplayName = "Title",
                Type = "string",
                Sensitivity = SensitivityLevel.Public
            }
        }
        });

        const int count = 1001;

        for (var i = 0; i < count; i++)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new()
                {
                    ["Title"] = $"Content {i}"
                },
                SearchText = null
            });
        }

        await session.SaveChangesAsync();

        await DataSeeder.BackfillSearchTextAsync(session);

        var contents = await session.Query<Content>()
            .Where(c => c.ContentType == type)
            .ToListAsync();

        contents.Should().HaveCount(count);
        contents.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.SearchText));
    }

    /// <summary>
    /// A run that dies partway says so, and says how far it got.
    /// </summary>
    /// <remarks>
    /// The seeder runs in an un-awaited Task.Run whose catch only logs, so a backfill that fails on a
    /// large corpus leaves the app serving traffic with public search empty for every pre-existing
    /// document. It used to leave nothing behind that distinguished that from a completed run. See
    /// issue #167.
    /// </remarks>
    [Fact]
    public async Task BackfillSearchText_ThatDoesNotFinish_SaysSo()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);

        try
        {
            var run = async () => await DataSeeder.BackfillSearchTextAsync(session, cancelled.Token);
            await run.Should().ThrowAsync<Exception>("a backfill that cannot finish must not return quietly");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        output.ToString().Should().Contain("SearchText backfill DID NOT COMPLETE");
        output.ToString().Should().NotContain("Backfilled SearchText for",
            "the completion line is what tells an operator the corpus is indexed");
    }

    /// <summary>
    /// The positive control. A backfill that reports failure whatever happens would pass the test
    /// above.
    /// </summary>
    [Fact]
    public async Task BackfillSearchText_ThatFinishes_SaysItCompleted()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);

        try
        {
            await DataSeeder.BackfillSearchTextAsync(session);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        output.ToString().Should().Contain("Completed:");
        output.ToString().Should().NotContain("DID NOT COMPLETE");
    }

    /// <summary>
    /// The demo content type is seeded into the table the API and the admin read.
    /// </summary>
    /// <remarks>
    /// It used to be written as a <c>Models.ContentType</c>, which nothing outside the seeder ever
    /// read: a fresh install logged "Created AttendanceRecord content type" while the content-types
    /// API returned an empty envelope, and the demo entries validated against no schema at all,
    /// because a content type with no definition is loose mode. See issue #322.
    /// </remarks>
    [Fact]
    public async Task The_seeded_demo_content_type_is_the_one_the_api_serves()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        await DataSeeder.SeedAttendanceContentTypeAsync(session);

        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == "AttendanceRecord");

        definition.Should().NotBeNull("the content-types API serves ContentTypeDefinition, and nothing else");
        definition!.Fields.Select(f => f.Name).Should().Contain(new[] { "FirstName", "LastName", "SSN" });
        definition.Fields.Single(f => f.Name == "SSN").Sensitivity
            .Should().Be(SensitivityLevel.Sensitive, "the demo is also the worked example");
    }

    /// <summary>
    /// And the demo records validate against it, rather than against nothing.
    /// </summary>
    [Fact]
    public async Task The_seeded_demo_records_validate_against_the_seeded_schema()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await DataSeeder.SeedAttendanceContentTypeAsync(session);

        var validator = scope.ServiceProvider
            .GetRequiredService<barakoCMS.Infrastructure.Services.IContentValidatorService>();

        foreach (var record in DataSeeder.SampleAttendanceRecords())
        {
            var (isValid, errors) = await validator.ValidateAsync("AttendanceRecord", record.Data);
            isValid.Should().BeTrue("the seeded demo data must satisfy the seeded schema, got: {0}",
                string.Join("; ", errors));
        }
    }
}
