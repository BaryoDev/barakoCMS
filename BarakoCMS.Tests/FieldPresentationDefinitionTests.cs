using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What the type validator says about a field's editor hint, section and role, and about a type's
/// route template. No database: this is the one check create, add field, a blueprint and a bundle
/// import all run.
/// </summary>
public class FieldPresentationDefinitionTests
{
    // Typed as the interface: ValidateRouteTemplate is the interface's own default body.
    private readonly IContentTypeValidatorService _validator = new ContentTypeValidatorService();

    private static FieldDefinition Field(
        string name, string type, string? editor = null, string? section = null, string? role = null) => new()
    {
        Name = name, DisplayName = name, Type = type, Editor = editor, Section = section, Role = role,
    };

    private List<string> Errors(params FieldDefinition[] fields) =>
        _validator.Validate("event", "Event", fields.ToList()).Errors;

    [Fact]
    public void A_field_with_no_editor_section_or_role_is_accepted_as_before()
    {
        Errors(Field("Title", "string"), Field("Blocks", "json")).Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_editor_is_refused_naming_the_accepted_values_and_not_what_was_sent()
    {
        var errors = Errors(Field("Sections", "json", editor: "kanban-board"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("Sections").And.Contain("editor");
        FieldPresentation.Editors.Should().HaveCount(4);
        foreach (var editor in FieldPresentation.Editors)
            errors[0].Should().Contain(editor.Name);
        errors[0].Should().NotContain("kanban-board");
    }

    [Theory]
    [InlineData("Blocks")]
    [InlineData("BLOCKS")]
    [InlineData(" blocks")]
    [InlineData("")]
    public void An_editor_in_another_case_padded_or_empty_is_refused(string editor)
    {
        Errors(Field("Sections", "json", editor: editor))
            .Should().ContainSingle().Which.Should().Contain("Accepted values");
    }

    [Theory]
    [InlineData("blocks", "json")]
    [InlineData("blocks", "array")]
    [InlineData("menu", "json")]
    [InlineData("links", "array")]
    [InlineData("image", "url")]
    [InlineData("image", "string")]
    [InlineData("image", "file")]
    public void A_known_editor_is_accepted_on_a_field_type_it_is_for(string editor, string type)
    {
        Errors(Field("Thing", type, editor: editor)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("blocks", "string")]
    [InlineData("menu", "bool")]
    [InlineData("links", "object")]
    [InlineData("image", "json")]
    [InlineData("image", "int")]
    public void A_known_editor_on_a_field_type_it_is_not_for_is_refused_naming_the_types(string editor, string type)
    {
        var errors = Errors(Field("Thing", type, editor: editor));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("Thing").And.Contain(editor).And.Contain(type);

        var spec = FieldPresentation.Editors.Single(e => e.Name == editor);
        spec.FieldTypes.Should().NotBeEmpty();
        foreach (var accepted in spec.FieldTypes)
            errors[0].Should().Contain(accepted);
    }

    [Fact]
    public void An_editor_on_a_field_of_an_unknown_type_is_reported_once_by_the_type_check()
    {
        var errors = Errors(Field("Thing", "wibble", editor: "blocks"));

        errors.Should().ContainSingle().Which.Should().Contain("invalid type");
    }

    [Theory]
    [InlineData("Branding")]
    [InlineData("Header and footer")]
    [InlineData("Marka")]
    public void A_section_is_accepted_as_free_text(string section)
    {
        Errors(Field("Logo", "url", section: section)).Should().BeEmpty();
    }

    [Fact]
    public void A_section_of_sixty_characters_is_accepted_and_one_of_sixty_one_is_refused()
    {
        FieldPresentation.MaxSectionLength.Should().Be(60);

        Errors(Field("Logo", "url", section: new string('a', 60))).Should().BeEmpty();
        Errors(Field("Logo", "url", section: new string('a', 61)))
            .Should().ContainSingle().Which.Should().Contain("Logo").And.Contain("60");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Branding")]
    [InlineData("Branding ")]
    [InlineData("Brand\ning")]
    public void A_blank_padded_or_multi_line_section_is_refused(string section)
    {
        Errors(Field("Logo", "url", section: section))
            .Should().ContainSingle().Which.Should().Contain("Logo").And.Contain("section");
    }

    [Fact]
    public void An_unknown_role_is_refused_naming_the_accepted_values_and_not_what_was_sent()
    {
        var errors = Errors(Field("EventName", "string", role: "headline-of-the-page"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("EventName").And.Contain("role");
        FieldPresentation.Roles.Should().HaveCount(3);
        foreach (var role in FieldPresentation.Roles)
            errors[0].Should().Contain(role.Name);
        errors[0].Should().NotContain("headline-of-the-page");
    }

    [Theory]
    [InlineData("title", "string")]
    [InlineData("title", "text")]
    [InlineData("summary", "text")]
    [InlineData("summary", "markdown")]
    [InlineData("date", "datetime")]
    [InlineData("date", "date")]
    public void A_known_role_is_accepted_on_a_field_type_it_is_for(string role, string type)
    {
        Errors(Field("Thing", type, role: role)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("title", "json")]
    [InlineData("summary", "bool")]
    [InlineData("date", "string")]
    [InlineData("Title", "string")]
    public void A_role_on_a_field_type_it_is_not_for_or_in_another_case_is_refused(string role, string type)
    {
        Errors(Field("Thing", type, role: role)).Should().ContainSingle().Which.Should().Contain("Thing");
    }

    [Fact]
    public void Two_fields_declaring_one_role_are_refused_naming_the_role_and_both_fields()
    {
        var errors = Errors(
            Field("EventName", "string", role: "title"),
            Field("Headline", "string", role: "title"),
            Field("Teaser", "text", role: "summary"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("'title'").And.Contain("EventName").And.Contain("Headline");
        errors[0].Should().NotContain("Teaser");
    }

    [Fact]
    public void One_field_for_each_role_is_accepted()
    {
        Errors(
            Field("EventName", "string", role: "title"),
            Field("Teaser", "text", role: "summary"),
            Field("StartsOn", "datetime", role: "date")).Should().BeEmpty();
    }

    [Fact]
    public void A_role_already_on_a_stored_field_is_refused_on_the_field_being_added()
    {
        var stored = Field("EventName", "string", role: "title");
        var added = Field("Headline", "string", role: "title");

        var (valid, errors) = _validator.Validate("event", "Event", [stored, added], [stored]);

        valid.Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("'title'");
    }

    [Theory]
    [InlineData("/blog/{slug}")]
    [InlineData("/{slug}")]
    [InlineData("/events/2026/{slug}/details")]
    [InlineData("/news_items/{slug}.html")]
    [InlineData("/.well-known/{slug}")]
    [InlineData("/blog/{slug}/")]
    public void A_route_template_that_is_a_path_holding_the_slug_once_is_accepted(string template)
    {
        var (valid, errors) = _validator.ValidateRouteTemplate(template);

        valid.Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_type_with_no_route_template_is_accepted()
    {
        _validator.ValidateRouteTemplate(null).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("blog/{slug}")]
    [InlineData("/blog")]
    [InlineData("/blog/{slug}/{slug}")]
    [InlineData("@other.example/{slug}")]
    [InlineData("https://other.example/{slug}")]
    [InlineData("/blog/{slug}?ref=feed")]
    [InlineData("/blog/{slug}#top")]
    [InlineData("/blog/{id}/{slug}")]
    [InlineData("/blog/<b>/{slug}")]
    [InlineData("/blog posts/{slug}")]
    [InlineData("/blog\\{slug}")]
    [InlineData("//other.example/{slug}")]
    [InlineData("/blog//{slug}")]
    [InlineData("/../{slug}")]
    [InlineData("/blog/../{slug}")]
    [InlineData("/./blog/{slug}")]
    [InlineData("/blog/{slug}/..")]
    public void A_route_template_that_is_not_such_a_path_is_refused(string template)
    {
        var (valid, errors) = _validator.ValidateRouteTemplate(template);

        valid.Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("routeTemplate").And.Contain("/blog/{slug}");
    }

    /// <summary>
    /// A host may register its own validator. One written before the member existed gets the
    /// rule from the interface, so it refuses what the built-in one refuses.
    /// </summary>
    [Fact]
    public void A_validator_that_does_not_write_the_route_template_check_still_refuses_a_bad_template()
    {
        IContentTypeValidatorService older = new ValidatorWrittenBefore();

        older.ValidateRouteTemplate("/blog/{slug}").IsValid.Should().BeTrue();
        older.ValidateRouteTemplate(null).IsValid.Should().BeTrue();

        foreach (var template in new[] { "@other.example/{slug}", "//other.example/{slug}", "/no-slug" })
        {
            var (valid, errors) = older.ValidateRouteTemplate(template);

            valid.Should().BeFalse("'{0}' is not a path on the site", template);
            errors.Should().ContainSingle().Which.Should().Contain("routeTemplate");
        }
    }

    private sealed class ValidatorWrittenBefore : IContentTypeValidatorService
    {
        public (bool IsValid, List<string> Errors) Validate(string name, string displayName, List<FieldDefinition> fields) =>
            (true, new List<string>());

        public (bool IsValid, List<string> Errors) ValidateLifecycle(LifecycleDefinition? lifecycle) =>
            (true, new List<string>());
    }

    [Fact]
    public void A_route_template_longer_than_the_cap_is_refused_and_one_at_the_cap_is_accepted()
    {
        FieldPresentation.MaxRouteTemplateLength.Should().Be(200);
        const string tail = "/{slug}";

        var atTheCap = "/" + new string('a', 200 - 1 - tail.Length) + tail;
        atTheCap.Length.Should().Be(200);

        _validator.ValidateRouteTemplate(atTheCap).IsValid.Should().BeTrue();
        _validator.ValidateRouteTemplate("/a" + atTheCap[1..]).IsValid.Should().BeFalse();
    }
}
