using System.Text.Json;
using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Serialization;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// The schema.org block a delivered entry carries, built from its type's roles and only from what
/// delivery projected, with no database (#567).
/// </summary>
public class PublicStructuredDataTests
{
    private const string SecretAuthor = "Ana Secret-Reyes";

    private static FieldDefinition Field(
        string name, string type = "string", string? role = null,
        SensitivityLevel sensitivity = SensitivityLevel.Public) => new()
    {
        Name = name, DisplayName = name, Type = type, Role = role, Sensitivity = sensitivity,
    };

    private static ContentTypeDefinition Story(string? structured, SensitivityLevel authorSensitivity = SensitivityLevel.Public) => new()
    {
        Name = "story",
        DisplayName = "Story",
        IsPubliclyDeliverable = true,
        StructuredDataType = structured,
        Fields =
        [
            Field("Slug", "slug"),
            Field("Headline", role: FieldPresentation.TitleRole),
            Field("Teaser", "text", role: FieldPresentation.SummaryRole),
            Field("PublishedOn", "datetime", role: FieldPresentation.DateRole),
            Field("Cover", "url", role: FieldPresentation.ImageRole),
            Field("Byline", role: FieldPresentation.AuthorRole, sensitivity: authorSensitivity),
        ],
    };

    private static Content Entry(string headline = "Flood waters recede") => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "story",
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
        UpdatedAt = new DateTime(2026, 10, 8, 9, 30, 0, DateTimeKind.Utc),
        Data = new()
        {
            ["Slug"] = "flood",
            ["Headline"] = headline,
            ["Teaser"] = "The river is back inside its banks.",
            ["PublishedOn"] = "2026-10-07T06:00:00Z",
            ["Cover"] = "https://cdn.example.com/flood.jpg",
            ["Byline"] = SecretAuthor,
        },
    };

    private static PublicContentResponse Delivered(Content entry, ContentTypeDefinition type) =>
        PublicStructuredData.Attach(PublicDelivery.ToPublic(entry, type, "Slug")!, type);

    private static string Serialize(object value)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new ObjectJsonConverter());
        return JsonSerializer.Serialize(value, options);
    }

    [Fact]
    public void An_article_is_built_from_the_roles_of_its_type()
    {
        var block = Delivered(Entry(), Story(StructuredDataTypes.Article)).StructuredData;

        block.Should().NotBeNull();
        block!.Should().HaveCount(8);
        block["@context"].Should().Be("https://schema.org");
        block["@type"].Should().Be("Article");
        block["headline"].Should().Be("Flood waters recede");
        block["description"].Should().Be("The river is back inside its banks.");
        block["datePublished"].Should().Be("2026-10-07T06:00:00Z");
        block["dateModified"].Should().Be("2026-10-08T09:30:00Z");
        block["image"].Should().Be("https://cdn.example.com/flood.jpg");
        block["author"].Should().BeEquivalentTo(new Dictionary<string, object> { ["@type"] = "Person", ["name"] = SecretAuthor });
    }

    [Fact]
    public void A_type_that_declares_no_structured_data_type_emits_nothing_whatever_roles_it_has()
    {
        var item = Delivered(Entry(), Story(null));

        item.StructuredData.Should().BeNull();
        Serialize(item).Should().NotContain("structuredData", "the key is absent rather than null");
    }

    [Theory]
    [InlineData("article")]
    [InlineData("Recipe")]
    [InlineData("")]
    public void A_stored_type_a_save_would_refuse_emits_nothing(string stored)
    {
        Delivered(Entry(), Story(stored)).StructuredData.Should().BeNull();
        StructuredDataTypes.Errors(stored).Should().ContainSingle()
            .Which.Should().Contain("structuredDataType").And.Contain("Article");
    }

    [Fact]
    public void A_masked_field_never_reaches_the_block()
    {
        var item = Delivered(Entry(), Story(StructuredDataTypes.NewsArticle, SensitivityLevel.Sensitive));

        item.StructuredData.Should().NotBeNull();
        item.StructuredData!.Should().ContainKey("headline");
        item.StructuredData.Should().NotContainKey("author");
        Serialize(item).Should().NotContain(SecretAuthor);
    }

    [Fact]
    public void An_entry_with_no_title_emits_nothing()
    {
        Delivered(Entry(headline: "  "), Story(StructuredDataTypes.Article)).StructuredData.Should().BeNull();
    }

    [Fact]
    public void An_event_is_named_and_starts_on_its_date_and_a_product_has_no_date()
    {
        var evt = Delivered(Entry(), Story(StructuredDataTypes.Event)).StructuredData!;
        evt.Should().ContainKey("name").And.ContainKey("startDate").And.NotContainKey("headline")
            .And.NotContainKey("author").And.NotContainKey("dateModified");
        evt["startDate"].Should().Be("2026-10-07T06:00:00Z");

        var product = Delivered(Entry(), Story(StructuredDataTypes.Product)).StructuredData!;
        product.Should().ContainKey("name").And.NotContainKey("datePublished").And.NotContainKey("startDate");
    }

    [Fact]
    public void A_value_holding_quotes_controls_and_a_closing_script_tag_still_serializes_as_JSON()
    {
        const string hostile = "He said \"stop\"\n\t</script><script>alert(1)</script>\u0001\\";
        var item = Delivered(Entry(headline: hostile), Story(StructuredDataTypes.Article));

        using var parsed = JsonDocument.Parse(Serialize(item.StructuredData!));
        parsed.RootElement.GetProperty("headline").GetString().Should().Be(hostile);
    }

    [Fact]
    public void An_image_that_is_not_a_web_address_or_a_file_delivery_left_out_is_not_named()
    {
        var entry = Entry();
        entry.Data["Cover"] = "javascript:alert(1)";
        Delivered(entry, Story(StructuredDataTypes.Article)).StructuredData!.Should().NotContainKey("image");

        var type = Story(StructuredDataTypes.Article);
        type.Fields.Single(f => f.Name == "Cover").Type = FileFields.TypeName;
        var projected = PublicDelivery.ToPublic(Entry(), type, "Slug")!;

        var unresolved = projected with { Data = projected.Data.Where(kv => kv.Key != "Cover").ToDictionary() };
        PublicStructuredData.Build(unresolved, type)!.Should().NotContainKey("image");

        var file = new ResolvedFile(Guid.NewGuid(), "https://files.example.com/cover.png", "cover.png", "image/png", 10, null, null);
        var resolved = projected with { Data = new Dictionary<string, object>(projected.Data) { ["Cover"] = file } };
        PublicStructuredData.Build(resolved, type)!["image"].Should().Be("https://files.example.com/cover.png");
    }
}
