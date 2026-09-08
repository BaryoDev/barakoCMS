using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// What the shipped blueprints are allowed to steer somebody into (#669).
/// </summary>
/// <remarks>
/// <para>
/// Neither <c>richtext</c> nor <c>markdown</c> is sanitised. Both store and return the string that
/// was saved, so the author of an entry decides what a consumer renders. The difference is what a
/// consumer can safely do with the result: markdown is normally rendered by something that drops raw
/// HTML, and richtext exists to become HTML. A blueprint choosing richtext therefore hands anyone who
/// can edit content a script tag on every page that shows it, before the site author has made a
/// single decision.
/// </para>
/// <para>
/// These read the embedded resources rather than the files on disk, because the resources are what a
/// consumer of the package actually gets. A blueprint edited on disk and not embedded would pass a
/// file-based test and ship the old shape.
/// </para>
/// </remarks>
public class BlueprintFieldTypeTests
{
    private const string ResourcePrefix = "Blueprints/";

    private static IReadOnlyList<(string Name, JsonDocument Json)> BuiltIn()
    {
        var assembly = typeof(barakoCMS.Models.ContentTypeDefinition).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[ResourcePrefix.Length..], JsonDocument.Parse(reader.ReadToEnd()));
            })
            .ToList();
    }

    private static IEnumerable<(string Blueprint, string Type, string Field, string FieldType)> Fields()
    {
        foreach (var (name, doc) in BuiltIn())
        {
            foreach (var contentType in doc.RootElement.GetProperty("contentTypes").EnumerateArray())
            {
                var typeName = contentType.GetProperty("name").GetString()!;

                foreach (var field in contentType.GetProperty("fields").EnumerateArray())
                {
                    yield return (
                        name,
                        typeName,
                        field.GetProperty("name").GetString()!,
                        field.GetProperty("type").GetString()!);
                }
            }
        }
    }

    /// <summary>
    /// The control. Every assertion below runs over this collection, and an empty one would let all
    /// of them pass while proving nothing.
    /// </summary>
    [Fact]
    public void The_built_in_blueprints_are_embedded_and_readable()
    {
        var blueprints = BuiltIn();

        blueprints.Should().HaveCountGreaterThanOrEqualTo(4);
        blueprints.Select(b => b.Name).Should().Contain(
            new[] { "blog.json", "docs.json", "events.json", "portfolio.json" });

        Fields().Should().NotBeEmpty();
    }

    [Fact]
    public void No_shipped_blueprint_declares_a_richtext_field()
    {
        var offenders = Fields()
            .Where(f => f.FieldType == "richtext")
            .Select(f => $"{f.Blueprint}: {f.Type}.{f.Field}")
            .ToList();

        offenders.Should().BeEmpty(
            "richtext is returned unsanitised and exists to be rendered as HTML, so a shipped "
          + "blueprint choosing it gives every editor stored XSS on the frontend by default. Use "
          + "markdown, whose ordinary renderer drops raw HTML.");
    }

    /// <summary>
    /// The long-form fields are markdown, and there are still enough of them to matter.
    /// </summary>
    /// <remarks>
    /// The paired positive control. Deleting every body field, or retyping them as <c>string</c>,
    /// would satisfy "no richtext" and leave the blueprints useless, so the count floor is the half
    /// that says the fix is still a blueprint somebody would apply.
    ///
    /// Only <c>Body</c> and <c>Bio</c>. A <c>Description</c> is plain <c>text</c> in three of the
    /// four blueprints on purpose, because it is a one-line summary rather than a document.
    /// </remarks>
    [Fact]
    public void Every_body_and_bio_field_is_markdown()
    {
        var prose = Fields()
            .Where(f => f.Field is "Body" or "Bio")
            .ToList();

        prose.Should().HaveCountGreaterThanOrEqualTo(5, "the blueprints describe sites, which have prose in them");
        prose.Should().OnlyContain(f => f.FieldType == "markdown");
    }

    /// <summary>
    /// A reading time stored beside the body it describes drifts from it the first time somebody
    /// edits without touching the number. It is computable from the text at render.
    /// </summary>
    [Fact]
    public void No_shipped_blueprint_stores_a_reading_time()
    {
        Fields().Should().NotContain(f => f.Field == "ReadingTimeMinutes");
    }
}
