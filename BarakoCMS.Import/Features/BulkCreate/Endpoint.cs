using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Import.Features.BulkCreate;

public class Request
{
    public string ContentType { get; set; } = string.Empty;
    public List<Dictionary<string, object>> Records { get; set; } = new();
    /// <summary>When true, valid records are created and invalid ones reported; when false (default),
    /// any invalid record aborts the whole import and nothing is written.</summary>
    public bool ContinueOnError { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Published;
}

public class Response
{
    public int Created { get; set; }
    public int Failed { get; set; }
    public List<RowError> Errors { get; set; } = new();

    public class RowError
    {
        public int Row { get; set; }
        public List<string> Messages { get; set; } = new();
    }
}

/// <summary>
/// POST /api/import/content — bulk-create content items from mapped records (typically the output of
/// /api/import/analyze after column mapping). Reuses the CMS's content-type validation, per-type
/// create permission, and event-sourced creation; all creates commit in one transaction.
/// </summary>
public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentValidatorService _validator;
    private readonly IPermissionResolver _permissions;

    public Endpoint(IDocumentSession session, IContentValidatorService validator, IPermissionResolver permissions)
    {
        _session = session;
        _validator = validator;
        _permissions = permissions;
    }

    public override void Configure()
    {
        Post("/api/import/content");
        // No fixed roles: authorization is the target content type's own "create" permission (below).
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ContentType) || req.Records.Count == 0)
        {
            AddError("ContentType and at least one record are required.");
            await SendErrorsAsync(400, ct);
            return;
        }

        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        if (!await _permissions.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        // Validate every record first so an all-or-nothing import can reject before writing anything.
        var errors = new List<Response.RowError>();
        var valid = new List<(int Row, Dictionary<string, object> Data)>();
        for (var i = 0; i < req.Records.Count; i++)
        {
            var (isValid, msgs) = await _validator.ValidateAsync(req.ContentType, req.Records[i]);
            if (isValid) valid.Add((i, req.Records[i]));
            else errors.Add(new Response.RowError { Row = i, Messages = msgs });
        }

        if (errors.Count > 0 && !req.ContinueOnError)
        {
            // Surface the row-level errors without creating anything.
            await SendAsync(new Response { Created = 0, Failed = errors.Count, Errors = errors }, 400, ct);
            return;
        }

        foreach (var (_, data) in valid)
        {
            var id = Guid.NewGuid();
            var @event = new ContentCreated(id, req.ContentType, data, req.Status, userId);
            _session.Events.StartStream<Content>(id, @event);
            var content = new Content();
            content.Apply(@event);
            _session.Store(content);
        }

        // All content items (and their event streams) commit atomically.
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response
        {
            Created = valid.Count,
            Failed = errors.Count,
            Errors = errors
        }, cancellation: ct);
    }
}
