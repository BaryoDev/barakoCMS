using barakoCMS.Core.Interfaces;
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
    private readonly IContentWriter _contentWriter;
    private readonly IContentValidatorService _validator;
    private readonly IPermissionResolver _permissions;

    public Endpoint(IDocumentSession session, IContentValidatorService validator, IPermissionResolver permissions, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
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
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!await _permissions.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Lowered on both sides, like the duplicate-name check in the content type create endpoint. A
        // type stored before names were normalised carries whatever the caller typed, and an exact
        // match finds no definition at all: the batch cap below would be off and the public-field set
        // would come out empty.
        var lowered = req.ContentType.ToLower();
        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);

        // Validate every record first so an all-or-nothing import can reject before writing anything.
        var errors = new List<Response.RowError>();
        var valid = new List<(int Row, Dictionary<string, object> Data)>();
        for (var i = 0; i < req.Records.Count; i++)
        {
            var (isValid, msgs) = await _validator.ValidateAsync(req.ContentType, req.Records[i], existing: null);
            if (isValid) valid.Add((i, req.Records[i]));
            else errors.Add(new Response.RowError { Row = i, Messages = msgs });
        }

        // The validator caps a singleton type by counting what is in the database, and inside one
        // batch there is nothing in the database yet: every record passes the count and the whole
        // batch lands. The cap has to be applied to the batch as well, so the rule holds on the one
        // path that can create many entries in a single request.
        if (definition?.IsSingleton == true && valid.Count > 1)
        {
            foreach (var (row, _) in valid.Skip(1))
            {
                errors.Add(new Response.RowError
                {
                    Row = row,
                    Messages = [$"'{definition.DisplayName}' holds a single entry, so only one record of it can be imported."],
                });
            }

            valid = valid.Take(1).ToList();
        }

        if (errors.Count > 0 && !req.ContinueOnError)
        {
            // Surface the row-level errors without creating anything.
            await Send.ResponseAsync(new Response { Created = 0, Failed = errors.Count, Errors = errors }, 400, ct);
            return;
        }

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, data) in valid)
        {
            var id = Guid.NewGuid();

            var searchText = string.Join(
                ' ',
                data
                    .Where(kv => publicFields.Contains(kv.Key))
                    .Select(kv => kv.Value?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            var @event = new ContentCreated(id, req.ContentType, data, req.Status, userId, searchText, SensitivityLevel.Public, DateTime.UtcNow);

            await _contentWriter.CreateAsync(@event, ct);
        }
        // All content items (and their event streams) commit atomically.
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response
        {
            Created = valid.Count,
            Failed = errors.Count,
            Errors = errors
        }, cancellation: ct);
    }
}
