using barakoCMS.Models;
using BarakoCMS.Documents;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Documents;

public class DocumentTemplateTests
{
    private static ContentTypeDefinition Invoice() => new()
    {
        Name = "invoice",
        Fields =
        [
            new FieldDefinition { Name = "Number", Type = "string" },
            new FieldDefinition { Name = "Customer", Type = "string" },
            new FieldDefinition { Name = "TaxId", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            new FieldDefinition { Name = "Note", Type = "string", Sensitivity = SensitivityLevel.Hidden },
        ],
    };

    private static DocumentTemplate Template(string source, string fileName = "") =>
        new() { Slug = "invoice", ContentType = "invoice", Source = source, FileName = fileName };

    [Fact]
    public void Each_line_becomes_one_block_of_its_kind()
    {
        var parsed = DocumentTemplateParser.Parse("@page Letter\n# Invoice {{Number}}\n## Bill to\nHello\n---\n| A | B |\n\nEnd");

        parsed.Ok.Should().BeTrue();
        parsed.Page.Should().Be("Letter");
        parsed.Blocks.Select(b => b.Kind).Should().Equal(
            BlockKind.Title, BlockKind.Heading, BlockKind.Paragraph, BlockKind.Rule,
            BlockKind.Row, BlockKind.Gap, BlockKind.Paragraph);
        parsed.Blocks.Single(b => b.Kind == BlockKind.Row).Cells.Should().Equal("A", "B");
    }

    [Theory]
    [InlineData("@include https://example.com/x")]
    [InlineData("@font https://example.com/x.ttf")]
    [InlineData("Text\n@page A4")]
    [InlineData("@page Tabloid")]
    public void A_directive_other_than_page_at_the_top_is_refused(string source)
    {
        var parsed = DocumentTemplateParser.Parse(source);

        parsed.Ok.Should().BeFalse();
        parsed.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_row_with_too_many_columns_is_refused()
    {
        DocumentTemplateParser.Parse("| 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |").Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("TaxId")]
    [InlineData("data.TaxId")]
    [InlineData("Note")]
    public void A_template_naming_a_field_that_is_not_Public_is_refused(string hole)
    {
        var refusal = DocumentTemplateGuard.Refuse(Template($"# Invoice\nTax id: {{{{{hole}}}}}"), Invoice());

        refusal.Should().NotBeNull();
        refusal.Should().Contain("cannot leave");
    }

    [Fact]
    public void A_file_name_naming_a_field_that_is_not_Public_is_refused()
    {
        DocumentTemplateGuard.Refuse(Template("# Invoice", "{{TaxId}}.pdf"), Invoice()).Should().NotBeNull();
    }

    [Fact]
    public void A_template_naming_only_Public_fields_and_system_values_is_accepted()
    {
        DocumentTemplateGuard.Refuse(
            Template("# Invoice {{Number}}\n{{customer}} {{createdAt}} {{id}}"), Invoice()).Should().BeNull();
    }

    [Fact]
    public void A_hole_naming_no_field_is_refused()
    {
        DocumentTemplateGuard.Refuse(Template("{{Nmber}}"), Invoice()).Should().Contain("Nmber");
    }

    [Fact]
    public void A_template_is_refused_when_its_content_type_is_unknown()
    {
        DocumentTemplateGuard.Refuse(Template("# Invoice"), null).Should().NotBeNull();
    }

    [Fact]
    public void The_shared_check_finds_the_same_hole_the_request_composer_refuses()
    {
        var found = TemplateHoles.FirstNonPublic(["ok {{Number}}", "bad {{ TaxId }}"], Invoice());

        found.Should().NotBeNull();
        found!.Value.Field.Name.Should().Be("TaxId");
    }

    [Fact]
    public void An_entry_renders_to_a_pdf()
    {
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "invoice",
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Data = new() { ["Number"] = "INV-1", ["Customer"] = "Ana" },
        };
        var parsed = DocumentTemplateParser.Parse("# Invoice {{Number}}\n---\n| Item | Qty |\n| Pen | 2 |\nThanks, {{Customer}}.");

        var first = PdfRenderer.Render(parsed, content);
        var second = PdfRenderer.Render(parsed, content);

        first.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(first, 0, 5).Should().Be("%PDF-");
        second.Should().Equal(first, "the same entry renders to the same bytes");
    }

    [Fact]
    public void A_document_over_the_page_limit_is_refused()
    {
        var parsed = new ParsedTemplate("A4",
            Enumerable.Range(0, 3000).Select(_ => new Block(BlockKind.Paragraph, ["line"])).ToList(), []);

        var act = () => PdfRenderer.Render(parsed, new Content { ContentType = "invoice" });

        act.Should().Throw<DocumentTooLongException>();
    }

    [Fact]
    public void A_row_cell_longer_than_the_page_limit_is_refused_not_clipped()
    {
        var content = new Content
        {
            ContentType = "invoice",
            Data = new() { ["Number"] = string.Join(' ', Enumerable.Repeat("word", 100_000)) },
        };
        var parsed = DocumentTemplateParser.Parse("| {{Number}} | x |");
        parsed.Ok.Should().BeTrue();

        var act = () => PdfRenderer.Render(parsed, content);

        act.Should().Throw<DocumentTooLongException>();
    }
}
