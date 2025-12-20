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
        Assert.Equal(5, result.SystemVariables.Count);
        Assert.Contains(result.SystemVariables, v => v.Name == "{{id}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{contentType}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{status}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{createdAt}}");
        Assert.Contains(result.SystemVariables, v => v.Name == "{{updatedAt}}");
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
