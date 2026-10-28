using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Files;

/// <summary>
/// Who may read the bytes of a private file, or destroy it: the uploader, or a caller holding
/// <see cref="FileCapabilities.ManageAllFiles"/>. Shared by <c>Download</c>, <c>Delete</c> and
/// <see cref="FileStore"/> so they answer the same question the same way; see issue #547, where
/// two of them had drifted. <see cref="MayReadAsync"/> adds one more reader for download and
/// display only: a caller who may read an entry that holds the file in a FileFollowsEntry field.
/// Delete and attaching a file to an entry stay on <see cref="MayAccessAsync"/>.
/// </summary>
/// <remarks>
/// <c>upload_files</c> alone reaches list, describe and edit for every file in the tenant, and
/// download and delete for a file this account uploaded, as <c>docs/access-control.md</c> says. None
/// of list, describe or edit exposes bytes or destroys anything, and list already returns the same
/// record describe does, so an ownership check on describe alone would hide nothing.
/// Reading the bytes and deleting them are the two routes that leave the caller with something they
/// did not have before (the file's content) or take something away for good, so both need this
/// check in addition to the capability gate.
///
/// The override used to be the role names Admin and SuperAdmin, read off the token. It is a
/// capability, answered from the caller's stored roles in the current tenant, so a role of any
/// name can be given it and a name in a token decides nothing. The names still open it while
/// <c>Auth:LegacyRoleFallback</c> is on, the rule every capability gate follows.
/// </remarks>
internal static class FileAccessRule
{
    public static async Task<bool> MayAccessAsync(
        ClaimsPrincipal caller,
        StoredFile file,
        string tenantSlug,
        IPermissionResolver permissions,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!IsSignedInHere(caller, tenantSlug))
        {
            return false;
        }

        if (FileOwnership.CanAccess(caller, file))
        {
            return true;
        }

        if (FileCapabilities.Defaults.LegacyRoles.Any(caller.IsInRole)
            && configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false))
        {
            return true;
        }

        return Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
            && userId != Guid.Empty
            && await permissions.HasCapabilityAsync(userId, FileCapabilities.ManageAllFiles, ct);
    }

    /// <summary>
    /// Download only: <see cref="MayAccessAsync"/>, or an entry the caller may read that holds the
    /// file in a field marked FileFollowsEntry. Delete keeps asking <see cref="MayAccessAsync"/>.
    /// </summary>
    public static async Task<bool> MayReadAsync(
        ClaimsPrincipal caller,
        StoredFile file,
        string tenantSlug,
        IQuerySession session,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        IConfiguration configuration,
        CancellationToken ct,
        Func<Task<IReadOnlyList<EntryFileAccess.FollowedField>>>? followedFields = null)
    {
        if (await MayAccessAsync(caller, file, tenantSlug, permissions, configuration, ct))
        {
            return true;
        }

        if (!IsSignedInHere(caller, tenantSlug)
            || !Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId)
            || userId == Guid.Empty)
        {
            return false;
        }

        var user = await session.LoadAsync<User>(userId, ct);
        return user is not null && await EntryFileAccess.MayReadViaEntryAsync(
            file, caller, user, session, permissions, sensitivity, followedFields is null ? null : await followedFields(), ct);
    }

    /// <summary>
    /// Whether <paramref name="caller"/> is a principal the authenticated routes of this module
    /// could have been reached with, asked before anything about one file.
    /// </summary>
    /// <remarks>
    /// Three checks that cost nothing and that a principal handed to a method has not had. An API
    /// key reaches no file route at all. A token names the tenant it was issued for, its role
    /// claims are that tenant's, and a request carrying it into another tenant is refused; the
    /// comparison is the one that refusal makes. On the module's own routes the request pipeline
    /// has made all three already, so there they change no answer.
    ///
    /// It is not the whole request pipeline. A revoked token, a token older than its user's
    /// session, a tenant that is switched off or is the default one in Multi mode, and a device
    /// that is not trusted are all refused before a route runs, and none of them is asked again
    /// here. The caller has to be the principal of the request this scope serves.
    /// </remarks>
    public static bool IsSignedInHere(ClaimsPrincipal caller, string tenantSlug)
    {
        if (caller.Identity is not { IsAuthenticated: true } || caller.HasClaim("auth_method", "apikey"))
        {
            return false;
        }

        var claimed = caller.FindFirst("tenant")?.Value;
        return string.IsNullOrEmpty(claimed) || string.Equals(claimed, tenantSlug, StringComparison.OrdinalIgnoreCase);
    }
}
