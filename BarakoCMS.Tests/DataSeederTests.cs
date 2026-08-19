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

    [Fact]
    public async Task BackfillSearchText_DoesNotCountContentWithoutDefinitionAsUpdated()
    {
        var type = $"missing_definition_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new Content
        {
            Id = Guid.NewGuid(),
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

}
