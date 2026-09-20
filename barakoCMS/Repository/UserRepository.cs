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

internal class MartenUserRepository(IDocumentSession session) : IUserRepository
{
    public async Task<User?> GetByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default)
    {
        var normalizedUsername = User.NormalizeIdentity(username);
        var normalizedEmail = User.NormalizeIdentity(email);
        return await session.Query<User>()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername || u.NormalizedEmail == normalizedEmail, ct);
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var normalized = User.NormalizeIdentity(username);
        return await session.Query<User>()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
    }

    public void Store(User user)
    {
        session.Store(user);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await session.SaveChangesAsync(ct);
    }
}
