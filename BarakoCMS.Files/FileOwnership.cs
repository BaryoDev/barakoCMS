using System.Security.Claims;

namespace BarakoCMS.Files;

/// <summary>
/// Whether a file is the caller's own upload. This is the half of <see cref="FileAccessRule"/>
/// that needs no lookup, and on its own it refuses everyone but the uploader.
/// </summary>
/// <remarks>
/// It used to let the role names Admin and SuperAdmin through as well, read off the token. That
/// override is the <c>manage_all_files</c> capability now, and <see cref="FileAccessRule"/> asks
/// for it. No role name is read here, so a caller that stops at this check grants less than the
/// rule, never more.
/// </remarks>
internal static class FileOwnership
{
    public static bool CanAccess(ClaimsPrincipal user, StoredFile file)
    {
        // The empty id owns nothing: a file stored for no user carries it as UploadedBy.
        return Guid.TryParse(user.FindFirst("UserId")?.Value, out var userId)
            && userId != Guid.Empty
            && file.UploadedBy == userId;
    }
}
