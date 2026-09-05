using System.Security.Claims;

namespace BarakoCMS.Files;

/// <summary>
/// Who may read the bytes of a file, or destroy it: the uploader, or an account administering the
/// tenant. Shared by <c>Download</c> and <c>Delete</c> so the two answer the same question the
/// same way; see issue #547, where they had drifted.
/// </summary>
/// <remarks>
/// <c>upload_files</c> alone reaches list, describe, edit and delete for a file this account
/// uploaded, and list and describe for anyone else's file in the tenant, because none of those
/// exposes bytes or destroys anything the caller could not already see through those same routes.
/// Reading the bytes and deleting them are the two routes that leave the caller with something they
/// did not have before (the file's content) or take something away for good, so both need this
/// check in addition to the capability gate. Until content can reference a file (#141) there is no
/// richer answer than "the person who uploaded it, or someone administering the tenant".
/// </remarks>
internal static class FileOwnership
{
    public static bool CanAccess(ClaimsPrincipal user, StoredFile file)
    {
        if (user.IsInRole("SuperAdmin") || user.IsInRole("Admin"))
        {
            return true;
        }

        return Guid.TryParse(user.FindFirst("UserId")?.Value, out var userId)
            && userId != Guid.Empty
            && file.UploadedBy == userId;
    }
}
