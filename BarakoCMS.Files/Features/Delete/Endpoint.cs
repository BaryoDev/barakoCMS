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
/// the uploader or an account administering the tenant, the same rule <c>Download</c> applies; see
/// <see cref="FileOwnership"/>.
/// </summary>
public class Endpoint : Endpoint<Request, Refusal>
{
    /// <summary>How many usages the refusal names. The usage route pages through the rest.</summary>
    private const int Named = 10;

    private readonly IDocumentSession _session;
    private readonly IFileStorage _storage;
    private readonly IPermissionResolver _permissions;
    private readonly ISensitivityService _sensitivity;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        IFileStorage storage,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _storage = storage;
        _permissions = permissions;
        _sensitivity = sensitivity;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/files/{id}");
        Definition.RequireCapability(FileCapabilities.UploadFiles, FileCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // upload_files alone opens list, describe and edit for anyone's upload in the tenant, none
        // of which exposes bytes or destroys anything the caller could not already see through those
        // same routes. Delete does destroy something, so it needs what Download already asks for:
        // the uploader, or an account administering the tenant. Before this check, a media editor
        // who could not download a stranger's file could still delete it (#547).
        //
        // Checked before the usage lookup below, not after: a caller who may not have the file at
        // all should not spend a database scan to be told so, and should not learn how many entries
        // reference something that is not theirs to remove.
        if (!FileOwnership.CanAccess(User, file))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);

        if (!req.Force)
        {
            var usages = FileUsage.Referencing(_session, file);
            var used = await usages.CountAsync(ct);
            if (used > 0)
            {
                var first = await usages.Take(Named).ToListAsync(ct);
                var caller = userId == Guid.Empty
                    ? null
                    : await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);

                await Send.ResponseAsync(new Refusal
                {
                    Message = $"This file is used by {used} {(used == 1 ? "entry" : "entries")}. "
                            + "Delete with ?force=true to remove it anyway.",
                    Total = used,
                    Usages = await FileUsage.RowsAsync(first, caller, _permissions, _sensitivity, HttpContext, ct),
                }, 409, ct);
                return;
            }
        }

        // The resizes go with their original: they are reachable only through it, so a variant
        // outliving its parent would be bytes nothing can ever serve again.
        var variants = await _session.Query<StoredFile>()
            .Where(v => v.ParentFileId == file.Id)
            .ToListAsync(ct);

        foreach (var variant in variants)
        {
            await _storage.DeleteAsync(variant.StorageKey, ct);
            _session.Delete(variant);
        }

        await _storage.DeleteAsync(file.StorageKey, ct);
        _session.Delete(file);

        await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
            _session,
            _tenant.Slug,
            "file.deleted",
            userId,
            User.FindFirst("Username")?.Value ?? string.Empty,
            targetType: "file",
            targetId: file.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["fileName"] = file.FileName,
                ["forced"] = req.Force,
                ["variants"] = variants.Count,
            },
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        await _session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
