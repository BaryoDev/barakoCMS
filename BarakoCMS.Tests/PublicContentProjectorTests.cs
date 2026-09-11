using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The module-facing projection. Every assertion here is about one of the four checks that stand
/// between a stored document and a public response, because the reason this interface exists is that
/// a module must not hold its own copy of them.
/// </summary>
public class PublicContentProjectorTests
{
    private static readonly IPublicContentProjector Projector =
        new barakoCMS.Infrastructure.Services.PublicContentProjector();

    private const string SecretValue = "topsecret-value-12345";

    private static ContentTypeDefinition Definition(bool deliverable = true, bool withSeo = false)
    {
        var def = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "post",
            DisplayName = "Post",
            IsPubliclyDeliverable = deliverable,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition
                {
                    Name = "Secret",
                    DisplayName = "Secret",
                    Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        };

        if (withSeo)
        {
            def.Fields.AddRange(barakoCMS.Features.Seo.SeoFields.Definitions());
        }

        return def;
    }

    private static Content Entry(
        ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public) => new()
        {
            Id = Guid.NewGuid(),
            ContentType = "post",
            Status = status,
            Sensitivity = sensitivity,
            Data = new()
            {
                ["Title"] = "Hello World",
                ["Slug"] = "hello-world",
                ["Secret"] = SecretValue,
                ["OrphanNote"] = "orphan-leak-xyz",
            },
        };

    [Fact]
    public void A_published_public_entry_is_projected()
    {
        var entry = Entry();

        var projected = Projector.Project(entry, Definition());

        projected.Should().NotBeNull();
        projected!.Id.Should().Be(entry.Id);
        projected.ContentType.Should().Be("post");
        projected.Slug.Should().Be("hello-world");
        projected.CreatedAt.Should().Be(entry.CreatedAt);
        projected.UpdatedAt.Should().Be(entry.UpdatedAt);
    }

    [Fact]
    public void Only_fields_the_type_marks_public_survive_the_projection()
    {
        var projected = Projector.Project(Entry(), Definition());

        projected.Should().NotBeNull();
        projected!.Data.Should().HaveCount(2, "Title and Slug are the only Public fields on the type");
        projected.Data.Keys.Should().BeEquivalentTo(["Title", "Slug"]);
        projected.Data.Should().NotContainKey("Secret", "the type marks it Sensitive");
        projected.Data.Should().NotContainKey("OrphanNote", "it matches no field in the schema at all");
        projected.Data.Values.Select(v => v?.ToString()).Should().NotContain(SecretValue);
    }

    [Fact]
    public void A_draft_is_not_projected()
    {
        Projector.Project(Entry(status: ContentStatus.Draft), Definition()).Should().BeNull();
    }

    [Fact]
    public void An_archived_entry_is_not_projected()
    {
        Projector.Project(Entry(status: ContentStatus.Archived), Definition()).Should().BeNull();
    }

    [Fact]
    public void A_document_marked_sensitive_is_not_projected()
    {
        Projector.Project(Entry(sensitivity: SensitivityLevel.Sensitive), Definition()).Should().BeNull();
        Projector.Project(Entry(sensitivity: SensitivityLevel.Hidden), Definition()).Should().BeNull();
    }

    [Fact]
    public void A_type_that_has_not_opted_into_delivery_is_not_projected()
    {
        Projector.Project(Entry(), Definition(deliverable: false)).Should().BeNull();
    }

    [Fact]
    public void An_entry_with_no_definition_is_not_projected()
    {
        Projector.Project(Entry(), null).Should().BeNull();
    }

    /// <summary>
    /// The pairing check. The core routes cannot reach this, because each one looks the definition up
    /// by the route's type and queries content by the same type, but the interface hands the pairing to
    /// a module and a wrong pair applies another type's field allowlist.
    /// </summary>
    [Fact]
    public void An_entry_projected_against_another_types_definition_is_not_projected()
    {
        var foreign = Definition();
        foreign.Name = "page";
        foreign.DisplayName = "Page";
        foreign.Fields.Single(f => f.Name == "Secret").Sensitivity = SensitivityLevel.Public;

        var entry = Entry();

        Projector.Project(entry, foreign).Should().BeNull(
            "the entry's own type is post, and post marks Secret Sensitive");

        /* Guards the assertion above: the same definition does project an entry of its own type, so
         * the null is the pairing check and not a deliverable definition that projects nothing. */
        var ownEntry = Entry();
        ownEntry.ContentType = "page";
        var ownProjection = Projector.Project(ownEntry, foreign);
        ownProjection.Should().NotBeNull();
        ownProjection!.Data.Should().ContainKey("Secret", "page marks it Public");
    }

    [Fact]
    public void The_pairing_check_ignores_the_case_of_the_type_name()
    {
        var entry = Entry();
        entry.ContentType = "POST";

        Projector.Project(entry, Definition()).Should().NotBeNull(
            "type names are matched case-insensitively everywhere else in delivery");
    }

    [Fact]
    public void A_type_that_has_not_opted_into_delivery_is_not_deliverable()
    {
        Projector.IsDeliverable(Definition()).Should().BeTrue();
        Projector.IsDeliverable(Definition(deliverable: false)).Should().BeFalse();
        Projector.IsDeliverable(null).Should().BeFalse("an unknown type and an un-opted-in one answer alike");
    }

    [Fact]
    public void The_slug_field_is_the_one_delivery_reads_back()
    {
        Projector.SlugField(Definition()).Should().Be("Slug");

        var noSlug = Definition();
        noSlug.Fields.RemoveAll(f => f.Name == "Slug");
        Projector.SlugField(noSlug).Should().BeNull("the type is not slug-addressable");

        Projector.SlugField(null).Should().BeNull(
            "an unfound definition can be passed straight in, as for the other two members");
    }

    [Fact]
    public void Seo_is_resolved_only_when_the_type_opted_in()
    {
        Projector.Project(Entry(), Definition())!.Seo.Should().BeNull("the type carries no SEO fields");

        var withSeo = Projector.Project(Entry(), Definition(withSeo: true));
        withSeo.Should().NotBeNull();
        withSeo!.Seo.Should().NotBeNull();
        withSeo.Seo!.Title.Should().Be("Hello World", "an unset meta title falls back to the entry's title");
    }

    /// <summary>
    /// The anti-drift assertion. The interface is only worth having if it is the same projection the
    /// core routes serve, so this compares it field by field against the function they call.
    /// </summary>
    [Fact]
    public void The_projection_is_the_one_the_delivery_routes_serve()
    {
        var entry = Entry();
        var def = Definition(withSeo: true);

        var fromEndpoint = PublicDelivery.ToPublic(entry, def, PublicDelivery.SlugField(def));
        var fromProjector = Projector.Project(entry, def);

        fromEndpoint.Should().NotBeNull("otherwise both sides are null and this proves nothing");
        fromProjector.Should().NotBeNull();

        fromProjector!.Id.Should().Be(fromEndpoint!.Id);
        fromProjector.ContentType.Should().Be(fromEndpoint.ContentType);
        fromProjector.Slug.Should().Be(fromEndpoint.Slug);
        fromProjector.CreatedAt.Should().Be(fromEndpoint.CreatedAt);
        fromProjector.UpdatedAt.Should().Be(fromEndpoint.UpdatedAt);
        fromProjector.Data.Should().NotBeEmpty();
        fromProjector.Data.Should().BeEquivalentTo(fromEndpoint.Data);
        fromProjector.Seo.Should().NotBeNull();
        fromProjector.Seo.Should().BeEquivalentTo(fromEndpoint.Seo);
    }

    /// <summary>
    /// The interface and its result types are the whole point of the change, and a module in another
    /// assembly can only reach them if they are exported.
    /// </summary>
    [Fact]
    public void The_projector_is_part_of_the_package_surface()
    {
        var exported = typeof(barakoCMS.Modules.IBarakoModule).Assembly
            .GetExportedTypes()
            .Select(t => t.FullName)
            .ToArray();

        exported.Should().NotBeEmpty();
        exported.Should().Contain("barakoCMS.Core.Interfaces.IPublicContentProjector");
        exported.Should().Contain("barakoCMS.Core.Interfaces.PublicContentProjection");
        exported.Should().Contain("barakoCMS.Core.Interfaces.PublicSeoMetadata");
    }
}
