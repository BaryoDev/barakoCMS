using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Files.Features.Usage;

public class Request : PaginatedRequest
{
    public Guid Id { get; set; }
}

/// <summary>
/// GET /api/files/{id}/usage. The entries in this tenant whose data references the file, newest
/// change first. See <see cref="FileUsage"/> for what counts as a reference and what a row shows.
/// </summary>
public class Endpoint : Endpoint<Request, PaginatedResponse<FileUsageRow>>
{
    private readonly IQuerySession _session;
    private readonly IPermissionResolver _permissions;
    private readonly ISensitivityService _sensitivity;

    public Endpoint(IQuerySession session, IPermissionResolver permissions, ISensitivityService sensitivity)
    {
        _session = session;
        _permissions = permissions;
        _sensitivity = sensitivity;
    }

    public override void Configure()
    {
        Get("/api/files/{id}/usage");
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

        var usages = FileUsage.Referencing(_session, file);
        var total = await usages.CountAsync(ct);
        var page = await usages.Skip(req.Skip).Take(req.Take).ToListAsync(ct);

        var caller = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            ? await _session.LoadAsync<barakoCMS.Models.User>(userId, ct)
            : null;

        await Send.OkAsync(new PaginatedResponse<FileUsageRow>
        {
            Items = await FileUsage.RowsAsync(page, caller, _permissions, _sensitivity, HttpContext, ct),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, ct);
    }
}
