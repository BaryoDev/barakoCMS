namespace barakoCMS.Core.Interfaces;

/// <summary>
/// A named, read-only source of data a page block can bind to. A module registers one as a
/// singleton <see cref="IBlockDataSource"/>, and <c>GET /api/pages/data-sources</c> lists it.
/// </summary>
/// <remarks>
/// A source says what it is and what its rows look like. It does not read anything here: the
/// module that registers it decides how its rows reach a page. The list leaves a source out for a
/// caller who does not hold <see cref="Capability"/>.
/// </remarks>
public interface IBlockDataSource
{
    /// <summary>
    /// A short, stable id, unique across the deployment: a lowercase letter, then lowercase letters,
    /// digits, <c>.</c>, <c>_</c> or <c>-</c>, at most 64 characters. For example <c>forms</c>.
    /// </summary>
    string Id { get; }

    /// <summary>What each row of the source carries.</summary>
    BlockDataSourceShape Shape { get; }

    /// <summary>The capability that reads this source's rows, or null when any signed-in caller may see it listed.</summary>
    string? Capability { get; }
}

/// <summary>What each row of a data source carries.</summary>
public sealed record BlockDataSourceShape(IReadOnlyList<BlockDataSourceField> Fields);

/// <summary>One field of a row: its name, and a kind such as <c>string</c>, <c>number</c>, <c>bool</c>, <c>date</c>, <c>id</c> or <c>object</c>.</summary>
public sealed record BlockDataSourceField(string Name, string Kind);
