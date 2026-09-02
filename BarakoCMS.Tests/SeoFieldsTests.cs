using FluentAssertions;
using barakoCMS.Features.Seo;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// How SEO metadata resolves, and the fallback that is the reason this is not just five fields.
/// </summary>
public class SeoFieldsTests
{
    private static Dictionary<string, object> Data(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void An_unset_meta_title_falls_back_to_the_entry_title()
    {
        // An empty title tag is worse than no tag. A search engine shown one indexes the page with
        // nothing to display; a page with no tag gets a title chosen from its content.
        var seo = SeoFields.Resolve(Data(("Title", "Spring roast notes")));

        seo.Title.Should().Be("Spring roast notes");
    }

    [Fact]
    public void A_meta_title_wins_over_the_entry_title()
    {
        // The pairing. A fallback that always won would make the field pointless, and it is the
        // field an editor actually came to fill in.
        var seo = SeoFields.Resolve(Data(
            ("Title", "Spring roast notes"),
            ("MetaTitle", "Spring roast notes | Barako Coffee")));

        seo.Title.Should().Be("Spring roast notes | Barako Coffee");
    }

    [Fact]
    public void An_entry_with_no_title_of_any_kind_resolves_to_null_rather_than_an_empty_string()
    {
        // Null is absent from the JSON, so a frontend renders no tag. An empty string would render
        // an empty one, which is the outcome the fallback exists to avoid.
        SeoFields.Resolve(Data(("Body", "words"))).Title.Should().BeNull();
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("DisplayName")]
    [InlineData("Label")]
    [InlineData("Subject")]
    [InlineData("Heading")]
    public void The_fallback_uses_the_same_names_the_admin_uses(string field)
    {
        // Same list, same order as admin/src/lib/content-title.ts. Two lists would answer
        // differently the first time one of them gained a name, and the symptom is a page whose tab
        // and whose search result disagree.
        SeoFields.Resolve(Data((field, "a title"))).Title.Should().Be("a title");
    }

    [Fact]
    public void A_blank_meta_title_is_treated_as_unset()
    {
        // An editor clearing the box leaves an empty string, not a missing key. Treating that as set
        // would emit exactly the empty tag this is arranged to prevent.
        var seo = SeoFields.Resolve(Data(("Title", "Real title"), ("MetaTitle", "   ")));

        seo.Title.Should().Be("Real title");
    }

    [Fact]
    public void Fields_are_matched_without_regard_to_case()
    {
        // An entry can hold "metatitle" under a field declared "MetaTitle", and every other reader
        // in this codebase matches that way.
        SeoFields.Resolve(Data(("metatitle", "lower"))).Title.Should().Be("lower");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void The_no_index_flag_reads_the_same_whether_it_arrived_as_a_bool_or_its_text(
        object stored, bool expected)
    {
        // It arrives as a bool from an admin form and as its JSON text after a round trip. A flag
        // that answered differently depending on which would hide pages from the sitemap on some
        // deployments and not others.
        SeoFields.Resolve(Data(("NoIndex", stored))).NoIndex.Should().Be(expected);
    }

    [Fact]
    public void No_index_defaults_to_false_when_the_field_is_absent()
    {
        SeoFields.Resolve(Data(("Title", "x"))).NoIndex.Should().BeFalse(
            "an entry that says nothing about indexing wants to be indexed");
    }

    [Fact]
    public void Every_seo_field_is_public_and_optional()
    {
        var fields = SeoFields.Definitions();

        fields.Should().HaveCount(5);

        // Public, because the whole point is that a frontend reads them anonymously. Any of them
        // marked Sensitive would vanish for exactly the callers SEO is for.
        fields.Should().OnlyContain(f => f.Sensitivity == SensitivityLevel.Public);

        // Optional, because opting a type in must not make every existing entry invalid.
        fields.Should().OnlyContain(f => !f.IsRequired);
    }

    [Fact]
    public void A_type_is_opted_in_when_it_carries_the_meta_title_field()
    {
        var without = new ContentTypeDefinition { Name = "article", Fields = [] };
        var with = new ContentTypeDefinition { Name = "article", Fields = [.. SeoFields.Definitions()] };

        SeoFields.IsOptedIn(without).Should().BeFalse();
        SeoFields.IsOptedIn(with).Should().BeTrue();
    }
}
