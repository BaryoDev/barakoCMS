using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core.Interfaces;

/// <summary>The host's <see cref="IContentReader"/>, over a Marten query session.</summary>
internal sealed class SessionContentReader(IQuerySession session) : IContentReader
{
    private const int PageSize = 500;

    public Task<Content?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        session.LoadAsync<Content>(id, cancellationToken);

    public async Task<IReadOnlyList<Content>> ListAsync(
        string contentType, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        return await session.Query<Content>()
            .Where(c => c.ContentType == contentType)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountAsync(string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return session.Query<Content>().CountAsync(c => c.ContentType == contentType, cancellationToken);
    }

    public async IAsyncEnumerable<Content> ReadAllAsync(
        string contentType, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var skip = 0;
        while (true)
        {
            var page = await ListAsync(contentType, skip, PageSize, cancellationToken);
            foreach (var entry in page)
            {
                yield return entry;
            }

            if (page.Count < PageSize)
            {
                yield break;
            }

            skip += PageSize;
        }
    }
}
