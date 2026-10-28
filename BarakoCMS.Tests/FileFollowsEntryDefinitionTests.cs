using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class FileFollowsEntryDefinitionTests
{
    private readonly IContentTypeValidatorService _validator = new ContentTypeValidatorService();

    private static FieldDefinition Field(string name, string type, bool follows = false) => new()
    {
        Name = name, DisplayName = name, Type = type, FileFollowsEntry = follows,
    };

    private List<string> Errors(params FieldDefinition[] fields) =>
        _validator.Validate("document", "Document", fields.ToList()).Errors;

    [Fact]
    public void The_flag_on_a_text_field_is_refused()
    {
        var errors = Errors(Field("Title", "text", follows: true));
        errors.Should().ContainSingle();
        errors[0].Should().Contain("FileFollowsEntry").And.Contain("file");
    }

    [Fact]
    public void The_flag_on_a_file_field_is_accepted() =>
        Errors(Field("File", "file", follows: true)).Should().BeEmpty();

    [Fact]
    public void An_absent_flag_is_accepted_on_any_field() =>
        Errors(Field("Title", "text"), Field("File", "file")).Should().BeEmpty();
}
