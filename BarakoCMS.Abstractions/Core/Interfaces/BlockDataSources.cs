using System.Text.RegularExpressions;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The data sources the deployment's modules registered, by id. A source with a malformed id, no
/// fields, or an id another source already took is refused when the host starts, naming it, so two
/// modules cannot quietly shadow each other. Core registers it, so a block write can check a block's
/// <c>dataSource</c> against it whether or not the Pages module is enabled.
/// </summary>
public sealed partial class BlockDataSources
{
    public const int MaxIdLength = 64;

    private readonly SortedDictionary<string, IBlockDataSource> _byId = new(StringComparer.Ordinal);

    public BlockDataSources(IEnumerable<IBlockDataSource> sources)
    {
        foreach (var source in sources)
        {
            if (!IsValidId(source.Id))
            {
                throw new InvalidOperationException(
                    $"Block data source id '{source.Id}' ({source.GetType().Name}) is not valid. "
                    + $"Use a lowercase letter, then lowercase letters, digits, '.', '_' or '-', at most {MaxIdLength} characters.");
            }

            if (source.Shape is not { Fields.Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Block data source '{source.Id}' ({source.GetType().Name}) declares no fields.");
            }

            if (!_byId.TryAdd(source.Id, source))
            {
                throw new InvalidOperationException(
                    $"Block data source id '{source.Id}' is registered by both {_byId[source.Id].GetType().Name} and {source.GetType().Name}.");
            }
        }
    }

    /// <summary>Every source, ordered by id.</summary>
    public IReadOnlyList<IBlockDataSource> All => [.. _byId.Values];

    public bool Contains(string id) => _byId.ContainsKey(id);

    public IBlockDataSource? Find(string id) => _byId.GetValueOrDefault(id);

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();
}
