using System.Text.Json;
using barakoCMS.Infrastructure.OpenApi;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The served document's patterns are ECMA-262, so a regex that ends with <c>\z</c> in .NET is
/// served ending with <c>$</c>, which means the same thing there.
/// </summary>
[Collection("Integration1")]
public class OpenApiPatternTests(IntegrationTestFixture factory)
{
    private static IEnumerable<string> Patterns(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "pattern" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                    }

                    foreach (var nested in Patterns(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Patterns(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    [Fact]
    public async Task NoServedPatternEndsWithBackslashZ()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(factory);
        var patterns = Patterns(doc.RootElement).ToList();

        patterns.Should().Contain(@"^[\x21-\x7E]{16,256}$", "the id token nonce is a validator regex ending with \\z");
        patterns.Should().Contain("^[a-z0-9-]+$", "the S3 region is a validator regex ending with \\z");
        patterns.Where(p => EcmaPatternProcessor.ToEcma(p) != p).Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"^[a-z]+\z", "^[a-z]+$")]
    [InlineData("^[a-z]+$", "^[a-z]+$")]
    [InlineData(@"^a\\z", @"^a\\z")]
    [InlineData(@"^a\\\z", @"^a\\$")]
    [InlineData(@"^a\zb", @"^a\zb")]
    [InlineData(null, null)]
    public void ToEcma(string? pattern, string? expected) => EcmaPatternProcessor.ToEcma(pattern).Should().Be(expected);
}
