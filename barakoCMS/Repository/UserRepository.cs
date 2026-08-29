using Marten;
using barakoCMS.Models;

namespace barakoCMS.Repository;

// Internal, not public. CLAUDE.md section 1a says there is no repository pattern here, and freezing
// a public abstraction the architecture disavows is the worst of both. It stays for now because
// Login and Register are built on it and four unit tests mock it to exercise password rules without
// a database; replacing it with IDocumentSession is a refactor of the auth path, not a freeze.

internal interface IUserRepository
{
    Task<User?> GetByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    void Store(User user);
    Task SaveChangesAsync(CancellationToken ct = default);
}

internal class MartenUserRepository : IUserRepository
{
    private readonly IDocumentSession _session;

    public MartenUserRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<User?> GetByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default)
    {
        return await _session.Query<User>()
            .FirstOrDefaultAsync(u => u.Username == username || u.Email == email, ct);
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        return await _session.Query<User>()
            .FirstOrDefaultAsync(u => u.Username == username, ct);
    }

    public void Store(User user)
    {
        _session.Store(user);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _session.SaveChangesAsync(ct);
    }
}
