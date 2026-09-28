using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>Finds the stored refresh token a caller presented.</summary>
internal static class RefreshTokenLookup
{
    /// <remarks>
    /// By hash first. Rows written before tokens were hashed have no hash and hold the token in
    /// plain text, so those are matched on the plain value, and only those: a hashed row never
    /// matches on its Token field. Refreshing replaces a plain row with a hashed one, so they are
    /// gone within the seven day lifetime and nobody is signed out by the deploy.
    ///
    /// Pass the document session when the row will be updated, so Marten tracks its version for the
    /// optimistic concurrency guard on rotation.
    /// </remarks>
    public static async Task<RefreshToken?> FindAsync(IQuerySession session, string presented, CancellationToken ct)
    {
        var hash = RefreshToken.HashOf(presented);
        return await session.Query<RefreshToken>().FirstOrDefaultAsync(rt => rt.TokenHash == hash, ct)
            ?? await session.Query<RefreshToken>().FirstOrDefaultAsync(rt => rt.TokenHash == null && rt.Token == presented, ct);
    }
}
