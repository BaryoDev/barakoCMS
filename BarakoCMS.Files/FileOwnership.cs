using System.Security.Claims;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Files;

/// <summary>
/// Who may read the bytes of a file, or destroy it: the uploader, or an account administering the
/// tenant. Shared by <c>Download</c> and <c>Delete</c> so the two answer the same question the
/// same way; see issue #547, where they had drifted.
/// </summary>
/// <remarks>
/// <c>upload_files</c> alone reaches list, describe and edit for every file in the tenant, and
/// download and delete for a file this account uploaded, as <c>docs/access-control.md</c> says. None
/// of list, describe or edit exposes bytes or destroys anything, and list already returns the same
/// record describe does, so an ownership check on describe alone would hide nothing.
/// Reading the bytes and deleting them are the two routes that leave the caller with something they
/// did not have before (the file's content) or take something away for good, so both need this
/// check in addition to the capability gate. Until content can reference a file (#141) there is no
/// richer answer than "the person who uploaded it, or someone administering the tenant".
/// </remarks>
internal static class FileOwnership
{
    // The roles that administer a tenant's files. One list, so the request path and the path with
    // no request cannot come to name different roles.
    private static readonly string[] AnyFileRoles = ["SuperAdmin", "Admin"];

    public static bool CanAccess(ClaimsPrincipal user, StoredFile file)
    {
        if (AnyFileRoles.Any(user.IsInRole))
        {
            return true;
        }

        return Guid.TryParse(user.FindFirst("UserId")?.Value, out var userId)
            && userId != Guid.Empty
            && file.UploadedBy == userId;
    }

    /// <summary>
    /// The same answer for a user who is not making a request, such as the one who last saved the
    /// entry a workflow is running for.
    /// </summary>
    /// <remarks>
    /// The roles are read the way a token is issued: the user's own roles together with the roles
    /// of an active membership in <paramref name="tenantSlug"/>, compared by exact name as
    /// <see cref="ClaimsPrincipal.IsInRole"/> compares them. A user who no longer exists is refused
    /// even for a file they uploaded, since they could not sign in to download it.
    /// </remarks>
    public static async Task<bool> CanAccessAsync(
        IQuerySession session, string tenantSlug, Guid? userId, StoredFile file, CancellationToken ct)
    {
        if (userId is not { } id || id == Guid.Empty)
        {
            return false;
        }

        var user = await session.LoadAsync<User>(id, ct);
        if (user is null)
        {
            return false;
        }

        if (file.UploadedBy == id)
        {
            return true;
        }

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(session, user, tenantSlug, ct);
        if (roleIds.Count == 0)
        {
            return false;
        }

        var names = await session.Query<Role>()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        return names.Any(name => AnyFileRoles.Contains(name, StringComparer.Ordinal));
    }
}
