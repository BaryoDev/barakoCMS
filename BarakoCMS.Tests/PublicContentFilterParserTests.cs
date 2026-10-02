using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The module-facing filter parser answers the way the core list does: Public fields only, the
/// same caps, and a refused filter cannot be run as no filter.
/// </summary>
public class PublicContentFilterParserTests
{
    private static readonly IPublicContentFilterParser Parser =
        new PublicContentFilterParser(new ConfigurationBuilder().Build());

    private static ContentTypeDefinition Def() => new()
    {
        Name = "product",
        IsPubliclyDeliverable = true,
        Fields = new List<FieldDefinition>
        {
            new() { Name = "title", Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "cost", Type = "number", Sensitivity = SensitivityLevel.Sensitive },
        },
    };

    private static QueryCollection Query(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Value)));

    [Fact]
    public void A_filter_on_a_public_field_is_accepted()
    {
        var filter = Parser.Parse(Query(("filter[title][eq]", "hat")), Def());

        filter.Error.Should().BeNull();
        filter.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void A_request_with_no_filter_is_empty_and_changes_nothing()
    {
        var filter = Parser.Parse(Query(("q", "hat"), ("limit", "5")), Def());
        var query = Array.Empty<Content>().AsQueryable();

        filter.Error.Should().BeNull();
        filter.IsEmpty.Should().BeTrue();
        filter.Apply(query).Should().BeSameAs(query);
    }

    [Fact]
    public void A_filter_on_a_field_that_is_not_public_is_refused_and_cannot_be_applied()
    {
        var filter = Parser.Parse(Query(("filter[cost][lte]", "50")), Def());

        filter.Error.Should().NotBeNullOrEmpty();

        var apply = () => filter.Apply(Array.Empty<Content>().AsQueryable());
        apply.Should().Throw<InvalidOperationException>(
            "a caller that skipped the Error check must not get an unfiltered query back");
    }

    [Fact]
    public void Sort_is_not_read()
    {
        var filter = Parser.Parse(Query(("sort", "nope")), Def());

        filter.Error.Should().BeNull("the list refuses an unknown sort, and this parser reads filters only");
        filter.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_request_with_no_filter_is_not_checked_against_the_types_fields()
    {
        var def = Def();
        def.Fields.Add(new FieldDefinition { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public });
        def.Fields.Add(new FieldDefinition { Name = "Cost", Type = "number", Sensitivity = SensitivityLevel.Public });

        var none = Parser.Parse(Query(("q", "hat")), def);
        none.Error.Should().BeNull();
        none.IsEmpty.Should().BeTrue();

        var filter = Parser.Parse(Query(("filter[title][eq]", "hat")), def);
        filter.Error.Should().BeNull("two readable spellings of one name is not a reason to refuse");
        filter.IsEmpty.Should().BeFalse();

        Parser.Parse(Query(("filter[Cost][lte]", "50")), def).Error.Should().NotBeNullOrEmpty(
            "Cost has a Sensitive twin, cost, and the lookup would find its value");
    }

    [Fact]
    public void An_unknown_content_type_is_refused()
    {
        Parser.Parse(Query(), null).Error.Should().NotBeNullOrEmpty();
    }
}
