using barakoCMS.Features.Public;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Parsing and refusing delivery filters. Everything security-relevant about #140 is decided here,
/// before a query is ever built.
/// </summary>
/// <remarks>
/// The rule that matters: a field the caller cannot read must not be filterable. Filtering on a
/// Sensitive field is an oracle. A caller narrows a salary or a date of birth by watching which
/// entries come back, and the value never appears in a response, so nothing looks like a leak.
/// </remarks>
public class DeliveryQueryTests
{
    private static ContentTypeDefinition Def() => new()
    {
        Name = "product",
        IsPubliclyDeliverable = true,
        Fields = new List<FieldDefinition>
        {
            new() { Name = "title",  Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = "price",  Type = "number", Sensitivity = SensitivityLevel.Public },
            new() { Name = "cost",   Type = "number", Sensitivity = SensitivityLevel.Sensitive },
            new() { Name = "notes",  Type = "string", Sensitivity = SensitivityLevel.Hidden },
        },
    };

    private static DeliveryQuery Parse(params (string Key, string Value)[] pairs) =>
        DeliveryQuery.Parse(
            pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)), Def());

    [Fact]
    public void A_filter_on_a_public_field_is_accepted()
    {
        var q = Parse(("filter[price][lte]", "500"));

        q.IsValid.Should().BeTrue();
        q.Filters.Should().ContainSingle()
            .Which.Should().Be(new DeliveryFilter("price", FilterOp.Lte, "500", "number"));
    }

    [Theory]
    [InlineData("cost")]   // Sensitive
    [InlineData("notes")]  // Hidden
    public void A_filter_on_a_field_the_caller_cannot_read_is_refused(string field)
    {
        var q = Parse(($"filter[{field}][eq]", "1"));

        q.IsValid.Should().BeFalse(
            "filtering on a field that is never delivered lets a caller learn its value by "
            + "observing which entries match");
        q.Filters.Should().BeEmpty("a refused filter must not be applied at all");
    }

    /// <summary>
    /// The positive control. Without it, every refusal test above would pass against a parser that
    /// refuses everything, which is not the behaviour being asserted.
    /// </summary>
    [Fact]
    public void A_public_field_is_not_refused()
    {
        Parse(("filter[title][eq]", "hat")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_field_is_refused_rather_than_ignored()
    {
        var q = Parse(("filter[nope][eq]", "1"));

        q.IsValid.Should().BeFalse(
            "silently ignoring a filter returns more rows than asked for, and the caller cannot "
            + "tell that from a genuine empty result");
    }

    /// <summary>
    /// A Sensitive field and a field that does not exist must be indistinguishable in the response,
    /// or the error message itself becomes the oracle.
    /// </summary>
    [Fact]
    public void A_sensitive_field_and_a_missing_field_give_the_same_answer()
    {
        var sensitive = Parse(("filter[cost][eq]", "1")).Error;
        var missing = Parse(("filter[does-not-exist][eq]", "1")).Error;

        sensitive.Should().NotBeNull();
        sensitive!.Replace("cost", "X").Should().Be(missing!.Replace("does-not-exist", "X"),
            "the message must not reveal that 'cost' exists while 'does-not-exist' does not");
    }

    [Fact]
    public void An_unknown_operator_is_refused()
    {
        Parse(("filter[price][exec]", "1")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_malformed_filter_key_is_refused()
    {
        Parse(("filter[price]", "1")).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("price", "price", false)]
    [InlineData("-price", "price", true)]
    [InlineData("PRICE", "price", false)]  // canonical name from the schema, not the caller's casing
    public void Sort_accepts_a_public_field_and_a_leading_minus(string given, string field, bool desc)
    {
        var q = Parse(("sort", given));

        q.IsValid.Should().BeTrue();
        q.Sort.Should().Be(new DeliverySort(field, desc));
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("-notes")]
    [InlineData("unknown")]
    public void Sorting_by_a_field_the_caller_cannot_see_is_refused(string given)
    {
        Parse(("sort", given)).IsValid.Should().BeFalse(
            "ordering by a hidden field leaks its ordering, which is most of its value");
    }

    [Fact]
    public void The_number_of_filters_is_capped()
    {
        var many = Enumerable.Range(0, DeliveryQuery.MaxFilters + 1)
            .Select(i => ($"filter[price][gt]", i.ToString()))
            .ToArray();

        Parse(many).IsValid.Should().BeFalse(
            "arbitrary filter combinations against a JSONB column on an anonymous endpoint is a "
            + "denial-of-service surface");
    }

    [Fact]
    public void Exactly_the_cap_is_still_allowed()
    {
        var atCap = Enumerable.Range(0, DeliveryQuery.MaxFilters)
            .Select(i => ($"filter[price][gt]", i.ToString()))
            .ToArray();

        Parse(atCap).IsValid.Should().BeTrue("the cap is a maximum, not an exclusive bound");
    }

    [Fact]
    public void The_stored_field_name_is_the_schema_spelling_not_the_callers()
    {
        var q = Parse(("filter[PrIcE][eq]", "1"));

        q.Filters.Single().Field.Should().Be("price",
            "only a name the content type already declared may reach the query builder");
    }

    [Fact]
    public void No_query_parameters_is_a_valid_empty_query()
    {
        var q = Parse();

        q.IsValid.Should().BeTrue();
        q.Filters.Should().BeEmpty();
        q.Sort.Should().BeNull();
    }

    [Fact]
    public void An_unknown_content_type_is_refused()
    {
        DeliveryQuery.Parse(Array.Empty<KeyValuePair<string, string?>>(), null)
            .IsValid.Should().BeFalse();
    }
}
