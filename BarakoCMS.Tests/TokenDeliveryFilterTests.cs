using FluentAssertions;
using barakoCMS.Features.Public;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The anonymous filter and sort allowlist leaves a token out by its type, as delivery does, even
/// from a definition stored with the field Public, which no endpoint stores.
/// </summary>
public class TokenDeliveryFilterTests
{
    private static readonly ContentTypeDefinition Type = new()
    {
        Name = "ticket",
        IsPubliclyDeliverable = true,
        Fields =
        [
            new FieldDefinition { Name = "Title", Type = "string" },
            new FieldDefinition { Name = "ClaimToken", Type = "token", Sensitivity = SensitivityLevel.Public },
        ],
    };

    private static DeliveryQuery Parse(string key, string value) =>
        DeliveryQuery.Parse([new KeyValuePair<string, string?>(key, value)], Type);

    [Fact]
    public void An_anonymous_filter_on_a_token_is_refused_and_one_on_a_public_field_is_not()
    {
        Parse("filter[Title][eq]", "Open day").IsValid.Should().BeTrue();

        var refused = Parse("filter[ClaimToken][eq]", "0123456789abcdef");

        refused.IsValid.Should().BeFalse();
        refused.Error.Should().Contain("not filterable").And.NotContain("0123456789abcdef");
    }

    [Fact]
    public void An_anonymous_sort_on_a_token_is_refused()
    {
        Parse("sort", "Title").IsValid.Should().BeTrue();
        Parse("sort", "-ClaimToken").IsValid.Should().BeFalse();
    }
}
