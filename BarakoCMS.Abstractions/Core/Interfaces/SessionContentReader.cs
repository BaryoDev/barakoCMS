using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Core.Interfaces;

/// <summary>The host's <see cref="IContentReader"/>, over a Marten query session.</summary>
internal sealed class SessionContentReader(IQuerySession session, int pageSize = SessionContentReader.DefaultPageSize) : IContentReader
{
    internal const int DefaultPageSize = 500;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        // Keyset on the id column: a page boundary does not move when rows are inserted mid-read,
        // and each page is an index seek rather than an offset scan. Guid has no C# > operator, so
        // the comparison is Postgres's own uuid ordering, the same one OrderBy(Id) sorts by.
        var last = Guid.Empty;
        while (true)
        {
            var after = last;
            var page = await session.Query<Content>()
                .Where(c => c.ContentType == contentType)
                .Where(c => c.MatchesSql("d.id > ?", after))
                .OrderBy(c => c.Id)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            foreach (var entry in page)
            {
                yield return entry;
            }

            if (page.Count < pageSize)
            {
                yield break;
            }

            last = page[^1].Id;
        }
    }
}
