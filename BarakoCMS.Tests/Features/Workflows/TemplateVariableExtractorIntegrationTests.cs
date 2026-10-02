using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

[Collection("Sequential")]
public class TemplateVariableExtractorIntegrationTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IDocumentStore _store;

    public TemplateVariableExtractorIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _store = DocumentStore.For(_fixture.ConnectionString);
    }

    [Fact]
    public async Task GetVariablesAsync_ShouldReturnSystemVariables()
    {
        // Arrange
        using var session = _store.LightweightSession();
        var extractor = new TemplateVariableExtractor(session);

        // Act
        var result = await extractor.GetVariablesAsync("TestType");

        // Assert
        Assert.NotNull(result.SystemVariables);
        Assert.Equal(11, result.SystemVariables.Count);
        Assert.Contains(result.SystemVariables, v => v.Name == "{{id}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{contentType}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{status}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{createdAt}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{updatedAt}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{createdBy.name}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{createdBy.email}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{transition.name}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{transition.at}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{transition.by.name}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{transition.by.email}}");
    }

    /// <summary>
    /// Red without the change: the list had no formats. Every example in it is one the engine
    /// fills, checked against the engine and not against a second list written here.
    /// </summary>
    [Fact]
    public async Task The_variables_list_the_formats_and_each_listed_example_resolves()
    {
        using var session = _store.LightweightSession();
        var extractor = new TemplateVariableExtractor(session);

        var result = await extractor.GetVariablesAsync("TestType");

        Assert.Equal(6, result.Formats.Count);
        Assert.Contains(result.Formats, v => v.Name == "{{createdAt | date \"MMM d, h:mm tt\"}}");
        Assert.Contains(result.Formats, v => v.Name == "{{data.Field | money}}");
        Assert.Contains(result.Formats, v => v.Name == "{{duration createdAt updatedAt}}");

        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "TestType",
            CreatedAt = new DateTime(2026, 9, 14, 0, 30, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc),
            Data = new Dictionary<string, object> { ["Field"] = "12.5" },
        };
        var context = new TemplateContext(
            TimeZoneInfo.Utc,
            null,
            new TemplatePerson("maria", "maria@example.com"),
            new TemplateTransition("Approve", entry.UpdatedAt, new TemplatePerson("ramon", "ramon@example.com")));

        foreach (var listed in result.SystemVariables.Concat(result.Formats))
        {
            var resolved = TemplateVariableExtractor.Resolve(listed.Name, entry, TemplateValueEncoding.None, context);

            Assert.DoesNotContain("{{", resolved);
            Assert.Empty(TemplateExpression.Problems(listed.Name, onTransition: true));
        }
    }

    [Fact]
    public async Task GetVariablesAsync_WithSampleContent_ShouldExtractDataFields()
    {
        // Arrange
        using var session = _store.LightweightSession();
        var extractor = new TemplateVariableExtractor(session);

        var sampleContent = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "PurchaseOrder",
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object>
            {
                { "OrderNumber", "PO-12345" },
                { "TotalAmount", 1000.50 },
                { "IsApproved", true }
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        session.Store(sampleContent);
        await session.SaveChangesAsync();

        // Act
        var result = await extractor.GetVariablesAsync("PurchaseOrder");

        // Assert
        Assert.NotEmpty(result.DataFields);
        Assert.Contains(result.DataFields, v => v.Name == "{{data.OrderNumber}}" && v.Type == "string");
        Assert.Contains(result.DataFields, v => v.Name == "{{data.TotalAmount}}" && v.Type == "number");
        Assert.Contains(result.DataFields, v => v.Name == "{{data.IsApproved}}" && v.Type == "boolean");

        // Cleanup
        session.Delete(sampleContent);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task GetVariablesAsync_WithNoContent_ShouldReturnEmptyDataFields()
    {
        // Arrange
        using var session = _store.LightweightSession();
        var extractor = new TemplateVariableExtractor(session);

        // Act
        var result = await extractor.GetVariablesAsync("NonExistentType");

        // Assert
        Assert.NotNull(result.SystemVariables);
        Assert.Empty(result.DataFields);
    }
}
