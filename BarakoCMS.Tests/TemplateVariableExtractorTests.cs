using Xunit;
using FluentAssertions;
using Moq;
using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

public class TemplateVariableExtractorTests
{
    private static TemplateVariableExtractor Sut() => new(Mock.Of<IDocumentSession>());

    private static Content ContentWith(Dictionary<string, object> data) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ContentType = "Article",
        Status = ContentStatus.Published,
        Data = data
    };

    [Fact]
    public void ResolveVariables_ResolvesDataAndSystemVariables()
    {
        var content = ContentWith(new Dictionary<string, object> { { "Name", "Alice" } });

        Sut().ResolveVariables("Hi {{data.Name}} ({{contentType}}/{{status}})", content)
            .Should().Be("Hi Alice (Article/Published)");
    }

    [Fact]
    public void ResolveVariables_LeavesUnknownTokensUntouched()
    {
        var content = ContentWith(new Dictionary<string, object>());

        Sut().ResolveVariables("value={{data.Missing}}", content)
            .Should().Be("value={{data.Missing}}");
    }

    [Fact]
    public void ResolveVariables_DoesNotReResolveInjectedTokens()
    {
        // "Evil" contains a token that names another field. A single-pass resolver must NOT expand
        // it into the value of "Secret" — otherwise one field could exfiltrate another (injection).
        var content = ContentWith(new Dictionary<string, object>
        {
            { "Evil", "{{data.Secret}}" },
            { "Secret", "TOP-SECRET" }
        });

        Sut().ResolveVariables("{{data.Evil}}", content)
            .Should().Be("{{data.Secret}}");
    }
}
