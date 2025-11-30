using Marten;
using barakoCMS.Models;

namespace barakoCMS.Repository;

public interface IUserRepository
{
    Task<User?> GetByUsernameOrEmailAsync(string username, string email, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    void Store(User user);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public class MartenUserRepository : IUserRepository
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
