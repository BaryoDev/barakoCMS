using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class TemplateVariableExtractorTests
{
    private readonly Mock<IDocumentSession> _mockSession;
    private readonly TemplateVariableExtractor _extractor;

    public TemplateVariableExtractorTests()
    {
        _mockSession = new Mock<IDocumentSession>();
        _extractor = new TemplateVariableExtractor(_mockSession.Object);
    }

    [Fact]
    public void ResolveVariables_ShouldReplaceSystemVariables()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            ContentType = "PurchaseOrder",
            Status = ContentStatus.Published,
            CreatedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2024, 1, 2, 15, 30, 0, DateTimeKind.Utc),
            Data = new Dictionary<string, object>()
        };

        var template = "ID: {{id}}, Type: {{contentType}}, Status: {{status}}";

        // Act
        var result = _extractor.ResolveVariables(template, content);

        // Assert
        Assert.Contains("3fa85f64-5717-4562-b3fc-2c963f66afa6", result);
        Assert.Contains("PurchaseOrder", result);
        Assert.Contains("Published", result);
    }

    [Fact]
    public void ResolveVariables_ShouldReplaceDataFields()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "PurchaseOrder",
            Data = new Dictionary<string, object>
            {
                { "OrderNumber", "PO-12345" },
                { "CustomerName", "John Doe" },
                { "TotalAmount", "1000.50" }
            }
        };

        var template = "Order {{data.OrderNumber}} for {{data.CustomerName}}: ${{data.TotalAmount}}";

        // Act
        var result = _extractor.ResolveVariables(template, content);

        // Assert
        Assert.Equal("Order PO-12345 for John Doe: $1000.50", result);
    }

    [Fact]
    public void ResolveVariables_WithMissingDataField_ShouldLeaveUnchanged()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "PurchaseOrder",
            Data = new Dictionary<string, object>
            {
                { "OrderNumber", "PO-12345" }
            }
        };

        var template = "Order {{data.OrderNumber}}, Customer: {{data.NonExistentField}}";

        // Act
        var result = _extractor.ResolveVariables(template, content);

        // Assert
        Assert.Equal("Order PO-12345, Customer: {{data.NonExistentField}}", result);
    }

    [Fact]
    public void ResolveVariables_WithEmptyTemplate_ShouldReturnEmpty()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Test",
            Data = new()
        };

        // Act
        var result = _extractor.ResolveVariables("", content);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void ResolveVariables_WithNullValue_ShouldReplaceWithEmpty()
    {
        // Arrange
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Test",
            Data = new Dictionary<string, object>
            {
                { "Field1", null! }
            }
        };

        var template = "Value: {{data.Field1}}";

        // Act
        var result = _extractor.ResolveVariables(template, content);

        // Assert
        Assert.Equal("Value: ", result);
    }
}
