using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class OwnerHiddenFieldDefinitionTests
{
    private readonly IContentTypeValidatorService _validator = new ContentTypeValidatorService();

    private static FieldDefinition Field(string name, string type, string? listedIn = null) => new()
    {
        Name = name, DisplayName = name, Type = type, ShownWhenListedIn = listedIn,
    };

    private List<string> Errors(params FieldDefinition[] fields) =>
        _validator.Validate("member", "Member", fields.ToList()).Errors;

    [Fact]
    public void An_array_field_on_the_same_type_is_accepted()
    {
        Errors(Field("Phone", "string", "ShownFields"), Field("ShownFields", "array")).Should().BeEmpty();
    }

    [Fact]
    public void A_list_field_that_does_not_exist_is_refused()
    {
        var errors = Errors(Field("Phone", "string", "Nope"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("Phone").And.Contain("ShownWhenListedIn").And.Contain("does not exist");
    }

    [Fact]
    public void A_list_field_that_is_not_an_array_is_refused()
    {
        var errors = Errors(Field("Phone", "string", "ShownFields"), Field("ShownFields", "string"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("array");
    }

    [Fact]
    public void A_field_that_lists_itself_is_refused()
    {
        var errors = Errors(Field("ShownFields", "array", "ShownFields"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("itself");
    }

    [Fact]
    public void The_list_name_matches_ignoring_case()
    {
        Errors(Field("Phone", "string", "shownfields"), Field("ShownFields", "array")).Should().BeEmpty();
    }
}
