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
            new() { Name = "Location", Type = "geopoint", Sensitivity = SensitivityLevel.Public },
            new() { Name = "Depot",  Type = "geopoint", Sensitivity = SensitivityLevel.Sensitive },
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

    // ---- near: a centre and a radius, on a geopoint field the caller may read ----

    [Fact]
    public void A_near_filter_on_a_public_geopoint_field_is_accepted()
    {
        var q = Parse(("filter[location][near]", "6.5031,124.8469,60"));

        q.IsValid.Should().BeTrue(q.Error);
        q.Near.Should().Be(new DeliveryNear("Location", 6.5031, 124.8469, 60),
            "the schema spelling is stored, not the caller's");
        q.Filters.Should().BeEmpty("near is carried separately from the comparison filters");
    }

    [Fact]
    public void A_near_filter_on_a_sensitive_geopoint_field_is_refused()
    {
        var q = Parse(("filter[Depot][near]", "6.5031,124.8469,60"));

        q.IsValid.Should().BeFalse("which entries are within 1 km of a guess is the field's value");
        q.Near.Should().BeNull();
    }

    [Fact]
    public void A_near_filter_on_a_field_that_is_not_a_geopoint_is_refused()
    {
        Parse(("filter[price][near]", "6.5031,124.8469,60")).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("6.5031,124.8469")]          // no radius
    [InlineData("6.5031,124.8469,60,1")]     // too many parts
    [InlineData("here,124.8469,60")]         // not a number
    [InlineData("91,124.8469,60")]           // latitude out of range
    [InlineData("6.5031,181,60")]            // longitude out of range
    [InlineData("NaN,124.8469,60")]          // parses as a double and is still not a position
    [InlineData("")]
    public void A_malformed_centre_is_refused(string value)
    {
        var q = Parse(("filter[location][near]", value));

        q.IsValid.Should().BeFalse();
        q.Near.Should().BeNull("a refused filter must not be applied at all");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_non_positive_radius_is_refused(string radius)
    {
        Parse(("filter[location][near]", $"6.5031,124.8469,{radius}")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_radius_above_the_cap_is_refused_rather_than_clamped()
    {
        var atCap = Parse(("filter[location][near]", "6.5031,124.8469,1000"));
        var overCap = Parse(("filter[location][near]", "6.5031,124.8469,1000.5"));

        atCap.IsValid.Should().BeTrue("the cap is a maximum, not an exclusive bound");
        overCap.IsValid.Should().BeFalse(
            "a silently narrowed radius returns fewer rows than asked for with nothing saying so");
    }

    [Fact]
    public void The_radius_cap_is_configurable()
    {
        var q = DeliveryQuery.Parse(
            [new KeyValuePair<string, string?>("filter[location][near]", "6.5031,124.8469,50")],
            Def(), maxRadiusKm: 10);

        q.IsValid.Should().BeFalse("the configured cap is 10 km and the request asked for 50");
    }

    [Fact]
    public void A_second_near_filter_is_refused()
    {
        Parse(("filter[location][near]", "6.5031,124.8469,60"),
              ("filter[location][near]", "14.5995,120.9842,60"))
            .IsValid.Should().BeFalse("one distance per entry, so one centre per request");
    }

    [Fact]
    public void A_near_filter_counts_against_the_cap()
    {
        var pairs = Enumerable.Range(0, DeliveryQuery.MaxFilters)
            .Select(i => ("filter[price][gt]", i.ToString()))
            .Append(("filter[location][near]", "6.5031,124.8469,60"))
            .ToArray();

        Parse(pairs).IsValid.Should().BeFalse("it is the most expensive filter of all");
    }

    [Theory]
    [InlineData("distance", false)]
    [InlineData("-distance", true)]
    [InlineData("Distance", false)]
    public void Sort_by_distance_is_accepted_next_to_a_near_filter(string sort, bool desc)
    {
        // Sort before the filter on purpose: the query string does not promise an order.
        var q = Parse(("sort", sort), ("filter[location][near]", "6.5031,124.8469,60"));

        q.IsValid.Should().BeTrue(q.Error);
        q.DistanceSortDescending.Should().Be(desc);
        q.Sort.Should().BeNull("the computed distance replaces a field sort rather than joining it");
    }

    [Fact]
    public void Sort_by_distance_without_a_near_filter_is_refused()
    {
        var q = Parse(("sort", "distance"));

        q.IsValid.Should().BeFalse("there is no centre to measure from");
        q.Error.Should().Contain("near filter", "the reason must say what is missing, not that 'distance' is an unknown field");
        q.DistanceSortDescending.Should().BeNull();
    }

    /// <summary>
    /// A type that already had a Public field called Distance keeps sorting by it. The reserved
    /// meaning only applies when there is a distance to sort by.
    /// </summary>
    [Fact]
    public void A_field_named_distance_still_sorts_when_there_is_no_near_filter()
    {
        var def = Def();
        def.Fields.Add(new FieldDefinition { Name = "Distance", Type = "number", Sensitivity = SensitivityLevel.Public });

        var q = DeliveryQuery.Parse([new KeyValuePair<string, string?>("sort", "distance")], def);

        q.IsValid.Should().BeTrue(q.Error);
        q.Sort.Should().Be(new DeliverySort("Distance", false));
        q.DistanceSortDescending.Should().BeNull();
    }

    /// <summary>
    /// The bounding box is a prefilter and must never be narrower than the circle it stands in for.
    /// </summary>
    [Theory]
    [InlineData(6.5031, 124.8469, 60)]
    [InlineData(64.1466, -21.9426, 300)]   // high latitude, where a flat-earth box is too narrow
    [InlineData(-33.8688, 151.2093, 1000)]
    public void The_bounding_box_contains_every_point_on_the_circle(double lat, double lng, double radius)
    {
        var near = new DeliveryNear("Location", lat, lng, radius);
        var (minLat, maxLat, minLng, maxLng) = DeliveryQuery.BoundingBox(near);

        // Walk the rim: for each bearing, the point at exactly the radius must sit inside the box.
        for (var bearing = 0; bearing < 360; bearing += 5)
        {
            var (pLat, pLng) = Destination(lat, lng, radius, bearing);
            pLat.Should().BeInRange(minLat, maxLat, "bearing {0}", bearing);
            pLng.Should().BeInRange(minLng, maxLng, "bearing {0}", bearing);
        }
    }

    [Fact]
    public void A_box_that_crosses_the_antimeridian_opens_to_the_full_longitude_range()
    {
        var (_, _, minLng, maxLng) = DeliveryQuery.BoundingBox(new DeliveryNear("Location", -16.5, 179.9, 100));

        (minLng, maxLng).Should().Be((-180d, 180d), "a wrapped box would exclude the far side");
    }

    // The point at a distance and bearing from a start, on the same sphere the query uses.
    private static (double Lat, double Lng) Destination(double lat, double lng, double km, double bearingDeg)
    {
        const double r = 6371.0088;
        var d = km / r;
        var brng = bearingDeg * Math.PI / 180;
        var lat1 = lat * Math.PI / 180;
        var lng1 = lng * Math.PI / 180;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(brng));
        var lng2 = lng1 + Math.Atan2(Math.Sin(brng) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return (lat2 * 180 / Math.PI, lng2 * 180 / Math.PI);
    }

    [Fact]
    public void An_unknown_content_type_is_refused()
    {
        DeliveryQuery.Parse(Array.Empty<KeyValuePair<string, string?>>(), null)
            .IsValid.Should().BeFalse();
    }
}
