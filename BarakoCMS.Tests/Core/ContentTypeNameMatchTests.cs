using FluentAssertions;
using barakoCMS.Core;
using Xunit;

namespace BarakoCMS.Tests.Core;

/// <summary>A content type name is matched ignoring case, and never matches a different name.</summary>
public class ContentTypeNameMatchTests
{
    [Theory]
    [InlineData("Order", "order")]
    [InlineData("order", "VISIT")]
    [InlineData("PurchaseOrder", "purchaseorder")]
    public void Names_that_differ_only_by_case_are_the_same_type(string a, string b) =>
        ContentTypeName.Same(a, b).Should().BeTrue();

    [Theory]
    [InlineData("Order", "Orders")]
    [InlineData("Order", " order")]
    [InlineData("purchase-order", "purchaseorder")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    public void Anything_else_is_a_different_type(string? a, string? b) =>
        ContentTypeName.Same(a, b).Should().BeFalse();

    [Fact]
    public void Pick_prefers_the_exact_spelling_over_one_that_differs_by_case()
    {
        string[] held = ["article", "Article"];

        ContentTypeName.PickByName(held, s => s, "Article").Should().Be("Article");
        ContentTypeName.PickByName(held, s => s, "article").Should().Be("article");
    }

    [Fact]
    public void Pick_falls_back_to_the_case_insensitive_match_and_never_to_another_name()
    {
        string[] held = ["article"];

        ContentTypeName.PickByName(held, s => s, "ARTICLE").Should().Be("article");
        ContentTypeName.PickByName(held, s => s, "articles").Should().BeNull();
    }
}
