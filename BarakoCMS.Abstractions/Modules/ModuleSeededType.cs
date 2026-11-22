namespace barakoCMS.Modules;

/// <summary>
/// What a module seeded for a content type's lifecycle, kept per tenant so a later edit of the
/// lifecycle can keep it.
/// </summary>
/// <remarks>
/// Written by the host's seed context each time a module seeds a type that declares a lifecycle,
/// whether or not the type already existed, from the definition the module passed. Not part of the
/// module API: a module never reads or writes it.
/// </remarks>
public sealed class ModuleSeededType
{
    /// <summary>A key made from the normalised type name, so a seed run finds its record again.</summary>
    public Guid Id { get; set; }

    /// <summary>The name of the type, as the module seeded it.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The <see cref="IBarakoModule.Name"/> of the module that seeded it.</summary>
    public string Module { get; set; } = string.Empty;

    public List<string> States { get; set; } = [];

    public string? InitialState { get; set; }

    public List<SeededTransition> Transitions { get; set; } = [];

    /// <summary>The key a type's record is stored under.</summary>
    public static Guid KeyOf(string? typeName)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(typeName);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("module-seeded-type:" + name));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Whether another record says the same thing, so writing it again can be skipped.</summary>
    public bool Same(ModuleSeededType other) =>
        string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
        && string.Equals(Module, other.Module, StringComparison.Ordinal)
        && string.Equals(InitialState, other.InitialState, StringComparison.Ordinal)
        && States.SequenceEqual(other.States, StringComparer.Ordinal)
        && Transitions.Select(t => (t.Name, t.From, t.To)).SequenceEqual(other.Transitions.Select(t => (t.Name, t.From, t.To)));
}

/// <summary>A transition a module seeded: its name and the states it moves between.</summary>
public sealed class SeededTransition
{
    public string Name { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
}
