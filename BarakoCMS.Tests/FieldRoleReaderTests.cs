using FluentAssertions;
using Microsoft.Extensions.Configuration;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Public;
using barakoCMS.Features.Seo;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// How a reader finds the field holding a role and the path an entry lives at, with no database.
/// The cases that matter most are the ones about stored data: a type with none of the new members,
/// and a stored value no save would accept.
/// </summary>
public class FieldRoleReaderTests
{
    private static ContentTypeDefinition TypeWith(string? routeTemplate, params FieldDefinition[] fields) => new()
    {
        Name = "event", DisplayName = "Event", RouteTemplate = routeTemplate, Fields = fields.ToList(),
    };

    private static FieldDefinition Field(string name, string? role = null) =>
        new() { Name = name, DisplayName = name, Type = "string", Role = role };

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void The_field_declaring_a_role_is_tried_ahead_of_the_names_guessed_before()
    {
        var type = TypeWith(null, Field("Title"), Field("EventName", role: "title"));

        FieldPresentation.FieldWithRole(type, FieldPresentation.TitleRole).Should().Be("EventName");
        FieldPresentation.Candidates(type, FieldPresentation.TitleRole, "Title", "Name")
            .Should().Equal("EventName", "Title", "Name");
    }

    [Fact]
    public void A_type_with_no_roles_and_a_missing_type_leave_the_guessed_names_as_they_were()
    {
        var type = TypeWith(null, Field("Title"), Field("Teaser"));

        FieldPresentation.FieldWithRole(type, FieldPresentation.TitleRole).Should().BeNull();
        FieldPresentation.Candidates(type, FieldPresentation.TitleRole, "Title", "Name").Should().Equal("Title", "Name");
        FieldPresentation.Candidates(null, FieldPresentation.DateRole, "Date", "PublishedAt")
            .Should().Equal("Date", "PublishedAt");
    }

    [Fact]
    public void A_stored_role_in_another_case_is_not_read_as_the_role()
    {
        var type = TypeWith(null, Field("EventName", role: "Title"));

        FieldPresentation.FieldWithRole(type, FieldPresentation.TitleRole).Should().BeNull();
    }

    [Fact]
    public void Of_two_stored_holders_of_a_role_the_first_in_the_types_order_is_read()
    {
        var type = TypeWith(null, Field("Headline", role: "title"), Field("EventName", role: "title"));

        FieldPresentation.FieldWithRole(type, FieldPresentation.TitleRole).Should().Be("Headline");
    }

    [Fact]
    public void A_types_route_template_is_read_ahead_of_the_configured_path()
    {
        var config = Config(("Feeds:Paths:event", "/configured/{slug}"));

        PublicDelivery.PathTemplate(TypeWith("/whats-on/{slug}"), config).Should().Be("/whats-on/{slug}");
    }

    [Fact]
    public void A_type_with_no_route_template_keeps_the_configured_path_and_then_the_default()
    {
        PublicDelivery.PathTemplate(TypeWith(null), Config(("Feeds:Paths:event", "/configured/{slug}")))
            .Should().Be("/configured/{slug}");
        PublicDelivery.PathTemplate(TypeWith(null), Config()).Should().Be("/event/{slug}");
    }

    [Theory]
    [InlineData("@other.example/{slug}")]
    [InlineData("https://other.example/{slug}")]
    [InlineData("/no-slug-here")]
    [InlineData("")]
    [InlineData("//other.example/{slug}")]
    [InlineData("/../{slug}")]
    [InlineData("/blog/./{slug}")]
    public void A_stored_route_template_no_save_would_accept_is_passed_over(string stored)
    {
        PublicDelivery.PathTemplate(TypeWith(stored), Config(("Feeds:Paths:event", "/configured/{slug}")))
            .Should().Be("/configured/{slug}");
        PublicDelivery.PathTemplate(TypeWith(stored), Config()).Should().Be("/event/{slug}");
    }

    [Fact]
    public void An_empty_meta_title_falls_back_to_the_field_holding_the_title_role()
    {
        var type = TypeWith(null, Field("Name"), Field("EventName", role: "title"));
        var data = new Dictionary<string, object> { ["Name"] = "internal-name", ["EventName"] = "Harvest Fair" };

        SeoFields.Resolve(data, type).Title.Should().Be("Harvest Fair");
        SeoFields.Resolve(data).Title.Should().Be("internal-name", "the control: with no type the guessed names decide");
    }

    [Fact]
    public void A_meta_title_still_wins_and_an_empty_title_field_falls_back_to_the_guessed_names()
    {
        var type = TypeWith(null, Field("Name"), Field("EventName", role: "title"));

        SeoFields.Resolve(
                new Dictionary<string, object> { ["MetaTitle"] = "Written for search", ["EventName"] = "Harvest Fair" }, type)
            .Title.Should().Be("Written for search");

        SeoFields.Resolve(
                new Dictionary<string, object> { ["Name"] = "internal-name", ["EventName"] = "  " }, type)
            .Title.Should().Be("internal-name");
    }
}
