using barakoCMS.Models;

namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Builds a <see cref="ContentTypeDefinition"/> with its fields.
///
/// Nine test files spell one of these out by hand, and they almost all want the same thing: a type
/// with a title and a slug. What differs is the odd extra field — a Sensitive one to prove masking, a
/// decimal to prove money survives a round trip — so that is what the API makes easy to say.
/// </summary>
public sealed class ContentTypeBuilder : BuilderBase<ContentTypeDefinition>
{
    private readonly List<FieldDefinition> _fields = new();
    private string? _name;
    private string? _displayName;
    private Guid? _id;

    /// <summary>Names the type. Left unset, a unique one is generated so tests cannot collide.</summary>
    private bool _deliverable;

    public ContentTypeBuilder Named(string name)
    {
        _name = name;
        return this;
    }

    public ContentTypeBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public ContentTypeBuilder WithDisplayName(string displayName)
    {
        _displayName = displayName;
        return this;
    }

    /// <summary>Adds a field. Type defaults to string, sensitivity to Public.</summary>
    public ContentTypeBuilder WithField(
        string name,
        string type = "string",
        SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        _fields.Add(new FieldDefinition
        {
            Name = name,
            DisplayName = name,
            Type = type,
            Sensitivity = sensitivity,
        });
        return this;
    }

    /// <summary>Title + Slug: what nearly every delivery, feed and preview test needs.</summary>
    public ContentTypeBuilder WithTitleAndSlug()
        => WithField("Title").WithField("Slug", "slug");

    /// <summary>A field the public projection must strip. Handy for asserting a leak did not happen.</summary>
    public ContentTypeBuilder WithSensitiveField(string name = "Secret")
        => WithField(name, "string", SensitivityLevel.Sensitive);

    /// <summary>
    /// Opt the type into the anonymous public delivery API. Not the default here, deliberately: the
    /// production default is off, and a builder that quietly opted every test type in would hide the
    /// very regression the gate exists to prevent.
    /// </summary>
    public ContentTypeBuilder PubliclyDeliverable(bool value = true)
    {
        _deliverable = value;
        return this;
    }

    public override ContentTypeDefinition Build()
    {
        var name = _name ?? Unique("type");
        return new ContentTypeDefinition
        {
            IsPubliclyDeliverable = _deliverable,
            Id = _id ?? Guid.NewGuid(),
            Name = name,
            DisplayName = _displayName ?? name,
            Fields = _fields.Count > 0 ? _fields : new List<FieldDefinition>(),
        };
    }
}
