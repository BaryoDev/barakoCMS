using barakoCMS.Core.Interfaces;
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
/// <remarks>
/// The constructor keeps the services it took before this endpoint moved onto
/// <see cref="IContentCreator"/>, because it is public in a published package. The creator is
/// resolved per request instead.
/// </remarks>
#pragma warning disable CS9113 // validator and contentWriter stay for the public constructor's sake
public class Endpoint(
    IDocumentSession session,
    IContentValidatorService validator,
    IPermissionResolver permissions,
    IContentWriter contentWriter) : Endpoint<Request, Response>
#pragma warning restore CS9113
{
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

        var user = await session.LoadAsync<User>(userId, ct);
        if (user == null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!await permissions.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Lowered on both sides, like the duplicate-name check in the content type create endpoint. A
        // type stored before names were normalised carries whatever the caller typed, and an exact
        // match finds no definition at all: the batch cap below would be off and the public-field set
        // would come out empty.
        var lowered = req.ContentType.ToLower();
        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);

        var maxRecords = ImportLimits.MaxRecords(Resolve<Microsoft.Extensions.Configuration.IConfiguration>());
        if (req.Records.Count > maxRecords)
        {
            AddError($"One request may create at most {maxRecords} records and this one holds {req.Records.Count}. "
                     + "Split the file, or raise Import:MaxRecords.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Every record goes through the content create write path, the same as POST /api/contents:
        // a field the caller may not see is dropped, the record is validated, and the type's
        // lifecycle hooks run. The batch carries the definition resolved above, and what earlier
        // rows claimed, so a singleton type takes one row and two rows cannot share a slug.
        var creator = Resolve<IContentCreator>();
        var batch = new ContentCreateBatch();
        if (definition is not null)
            batch.UseSchema(definition);

        // Checked in full before anything is staged, so an all-or-nothing import can refuse without
        // writing anything.
        var errors = new List<Response.RowError>();
        var valid = new List<ContentCreateRequest>();
        for (var i = 0; i < req.Records.Count; i++)
        {
            var request = new ContentCreateRequest
            {
                ContentType = req.ContentType,
                Data = req.Records[i] ?? new Dictionary<string, object>(),
                Status = req.Status,
            };

            var messages = await creator.CheckAsync(request, userId, HttpContext, batch, ct);
            if (messages.Count == 0) valid.Add(request);
            else errors.Add(new Response.RowError { Row = i, Messages = messages.ToList() });
        }

        if (errors.Count > 0 && !req.ContinueOnError)
        {
            // Surface the row-level errors without creating anything.
            await Send.ResponseAsync(new Response { Created = 0, Failed = errors.Count, Errors = errors }, 400, ct);
            return;
        }

        foreach (var request in valid)
            await creator.StageAsync(request, userId, batch, ct);

        // All content items (and their event streams) commit atomically.
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response
        {
            Created = valid.Count,
            Failed = errors.Count,
            Errors = errors
        }, cancellation: ct);
    }
}
