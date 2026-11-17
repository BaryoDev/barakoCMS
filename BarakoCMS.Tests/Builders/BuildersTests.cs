using Xunit;
using FluentAssertions;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Tests for the builders themselves.
///
/// A builder is fixture code, and a wrong one is worse than a wrong test: it makes other tests pass
/// for reasons nobody stated. The defaults are the part worth pinning — a user who cannot actually
/// sign in, or content that is Published when the test assumed Draft, would turn real assertions into
/// decoration.
/// </summary>
public class BuildersTests
{
    [Fact]
    public void Content_defaults_to_an_unpublished_public_entry()
    {
        var c = new ContentBuilder().Build();

        // Draft is the safe default: a test that forgets to say Published cannot accidentally assert
        // against something the delivery API would serve.
        c.Status.Should().Be(ContentStatus.Draft);
        c.Sensitivity.Should().Be(SensitivityLevel.Public);
        c.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Content_reads_as_the_sentence_the_test_is_making()
    {
        var c = new ContentBuilder()
            .OfType("post")
            .WithTitleAndSlug("Hello World")
            .Published()
            .Sensitive()
            .Build();

        c.ContentType.Should().Be("post");
        c.Status.Should().Be(ContentStatus.Published);
        c.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        c.Data["Title"].Should().Be("Hello World");
        c.Data["Slug"].Should().Be("hello-world");
    }

    [Fact]
    public void Content_ids_are_unique_so_two_builds_are_two_entries()
    {
        new ContentBuilder().Build().Id.Should().NotBe(new ContentBuilder().Build().Id);
    }

    [Fact]
    public void ContentType_generates_a_unique_name_when_none_is_given()
    {
        var a = new ContentTypeBuilder().Build();
        var b = new ContentTypeBuilder().Build();

        // Tests share one database, so two types with the same name would collide across files.
        a.Name.Should().NotBe(b.Name);
        a.DisplayName.Should().Be(a.Name);
    }

    [Fact]
    public void ContentType_carries_its_fields_and_their_sensitivity()
    {
        var t = new ContentTypeBuilder().Named("post").WithTitleAndSlug().WithSensitiveField().Build();

        t.Fields.Should().HaveCount(3);
        t.Fields.Single(f => f.Name == "Slug").Type.Should().Be("slug");
        t.Fields.Single(f => f.Name == "Secret").Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        t.Fields.Single(f => f.Name == "Title").Sensitivity.Should().Be(SensitivityLevel.Public);
    }
}
