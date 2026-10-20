using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Reads content entries for the current request's tenant. The read-side counterpart of
/// <see cref="IContentWriter"/>, so a module can look at content without naming a Marten type.
/// </summary>
/// <remarks>
/// It reads what is committed. An entry staged in the same unit of work and not saved yet is not
/// visible, as with any query. A list is always bounded: take a page, or use
/// <see cref="ReadAllAsync"/>, which pages internally.
/// </remarks>
public interface IContentReader
{
    /// <summary>The entry with this id, or null when there is none.</summary>
    Task<Content?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One page of the entries of a content type, in no guaranteed order.</summary>
    /// <param name="contentType">The content type name, compared exactly.</param>
    Task<IReadOnlyList<Content>> ListAsync(string contentType, int skip, int take, CancellationToken cancellationToken);

    /// <summary>How many entries a content type holds.</summary>
    Task<int> CountAsync(string contentType, CancellationToken cancellationToken);

    /// <summary>Every entry of a content type, read a page at a time.</summary>
    IAsyncEnumerable<Content> ReadAllAsync(string contentType, CancellationToken cancellationToken);
}
