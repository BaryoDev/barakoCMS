namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Base for test-data builders.
///
/// The suite hand-rolls its fixtures: <c>new Content { ... }</c> appears 40 times, <c>new User</c> 29,
/// <c>new FieldDefinition</c> 24. Every one of those spells out fields the test does not care about,
/// which buries the one field it does care about and makes a new test expensive enough to skip. A
/// builder supplies a valid default for everything and lets a test name only what matters to it.
///
/// Borrowed from Umbraco, which runs 54 of these across a suite of comparable shape.
/// </summary>
public abstract class BuilderBase<T>
{
    public abstract T Build();

    /// <summary>Unique-per-call suffix, so parallel or repeated tests cannot collide on a name.</summary>
    protected static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 40)];
}
