using barakoCMS.Models;

namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Builds a <see cref="Content"/> entry.
///
/// The most repeated fixture in the suite: 40 hand-written instances, most of them differing only in
/// status or sensitivity while restating the same four properties. Those two are exactly what the
/// delivery, feed, preview and scheduling tests turn on, so they get named methods and read as the
/// sentence the test is actually making — <c>Published().Sensitive()</c>.
/// </summary>
public sealed class ContentBuilder : BuilderBase<Content>
{
    private readonly Dictionary<string, object> _data = new();
    private string _contentType = "article";
    private ContentStatus _status = ContentStatus.Draft;
    private SensitivityLevel _sensitivity = SensitivityLevel.Public;
    private Guid? _id;
    private DateTime? _createdAt;
    private DateTime? _scheduledPublishAt;
    private DateTime? _scheduledUnpublishAt;

    public ContentBuilder OfType(string contentType)
    {
        _contentType = contentType;
        return this;
    }

    public ContentBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public ContentBuilder With(string field, object value)
    {
        _data[field] = value;
        return this;
    }

    /// <summary>Title and Slug together, since delivery keys off the slug and asserts on the title.</summary>
    public ContentBuilder WithTitleAndSlug(string title, string? slug = null)
    {
        _data["Title"] = title;
        _data["Slug"] = slug ?? title.ToLowerInvariant().Replace(' ', '-');
        return this;
    }

    public ContentBuilder Published()
    {
        _status = ContentStatus.Published;
        return this;
    }

    public ContentBuilder Draft()
    {
        _status = ContentStatus.Draft;
        return this;
    }

    public ContentBuilder Archived()
    {
        _status = ContentStatus.Archived;
        return this;
    }

    /// <summary>A document the public API must never return, whatever its status.</summary>
    public ContentBuilder Sensitive()
    {
        _sensitivity = SensitivityLevel.Sensitive;
        return this;
    }

    public ContentBuilder CreatedAt(DateTime when)
    {
        _createdAt = when;
        return this;
    }

    public ContentBuilder ScheduledToPublishAt(DateTime when)
    {
        _scheduledPublishAt = when;
        return this;
    }

    public ContentBuilder ScheduledToUnpublishAt(DateTime when)
    {
        _scheduledUnpublishAt = when;
        return this;
    }

    public override Content Build()
    {
        var now = DateTime.UtcNow;
        return new Content
        {
            Id = _id ?? Guid.NewGuid(),
            ContentType = _contentType,
            Status = _status,
            Sensitivity = _sensitivity,
            Data = _data,
            CreatedAt = _createdAt ?? now,
            UpdatedAt = _createdAt ?? now,
            ScheduledPublishAt = _scheduledPublishAt,
            ScheduledUnpublishAt = _scheduledUnpublishAt,
        };
    }
}
