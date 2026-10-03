using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Delete;

public class Request
{
    public Guid Id { get; set; }

    /// <summary>Delete even though entries still reference the file.</summary>
    [QueryParam]
    public bool Force { get; set; }
}

/// <summary>What a refused delete answers with: how many entries point at the file, and the first few.</summary>
public class Refusal
{
    public string Message { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<FileUsageRow> Usages { get; set; } = new();
}

/// <summary>
/// DELETE /api/files/{id}. Removes the record, its cached resizes and the bytes behind all of them.
/// Refused with a 409 while an entry still references the file, unless <c>?force=true</c>, so an
/// editor cannot break a page without being told which one. Refused with a 403 if the caller is not
/// the uploader and does not hold <c>manage_all_files</c>, the same rule <c>Download</c> applies; see
/// <see cref="FileAccessRule"/>.
/// </summary>
public class Endpoint(
    IDocumentSession session,
    IFileStorage storage,
    IPermissionResolver permissions,
    ISensitivityService sensitivity,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Refusal>
{
    /// <summary>How many usages the refusal names. The usage route pages through the rest.</summary>
    private const int Named = 10;

    public override void Configure()
    {
        Delete("/api/files/{id}");
        Definition.RequireCapability(FileCapabilities.UploadFiles, FileCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // upload_files alone opens list, describe and edit for anyone's upload in the tenant, none
        // of which exposes bytes or destroys anything the caller could not already see through those
        // same routes. Delete does destroy something, so it needs what Download already asks for:
        // the uploader, or a caller holding manage_all_files. Before this check, a media editor
        // who could not download a stranger's file could still delete it (#547).
        //
        // Checked before the usage lookup below, not after: a caller who may not have the file at
        // all should not spend a database scan to be told so, and should not learn how many entries
        // reference something that is not theirs to remove.
        if (!await FileAccessRule.MayAccessAsync(User, file, tenant.Slug, permissions, configuration, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        if (!req.Force)
        {
            var usages = FileUsage.Referencing(session, file);
            var used = await usages.CountAsync(ct);
            if (used > 0)
            {
                var first = await usages.Take(Named).ToListAsync(ct);
                var caller = userId == Guid.Empty
                    ? null
                    : await session.LoadAsync<barakoCMS.Models.User>(userId, ct);

                await Send.ResponseAsync(new Refusal
                {
                    Message = $"This file is used by {used} {(used == 1 ? "entry" : "entries")}. "
                            + "Delete with ?force=true to remove it anyway.",
                    Total = used,
                    Usages = await FileUsage.RowsAsync(first, caller, permissions, sensitivity, HttpContext, ct),
                }, 409, ct);
                return;
            }
        }

        await FileRemoval.RemoveAsync(
            session,
            storage,
            tenant.Slug,
            file,
            userId,
            User.FindFirst("Username")?.Value ?? string.Empty,
            req.Force,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        await Send.NoContentAsync(ct);
    }
}
