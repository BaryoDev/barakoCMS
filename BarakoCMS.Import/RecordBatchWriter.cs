using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Import.Features.BulkCreate;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Import;

/// <summary>What one batch of mapped records came to.</summary>
internal sealed record BatchResult(int Created, List<Response.RowError> Errors);

/// <summary>
/// Writes mapped records through the content create path in one transaction. Shared by the JSON
/// bulk create and the spreadsheet import, so the two cannot drift on what a row means.
/// </summary>
internal static class RecordBatchWriter
{
    /// <param name="rowNumbers">
    /// The number reported for each record in an error. Null reports the record's index.
    /// </param>
    /// <param name="loopLimit">
    /// Stops the row loop with an <see cref="OperationCanceledException"/> after this long. It does
    /// not apply to the commit: once every row is written the commit is not abandoned on a timer, so
    /// a timeout is never reported after a commit.
    /// </param>
    /// <param name="dryRun">Check and write everything, then roll back and keep nothing.</param>
    public static Task<BatchResult> WriteAsync(
        IContentBatchRunner runner,
        IPermissionResolver permissions,
        User user,
        ContentTypeDefinition? definition,
        string contentType,
        ContentStatus status,
        IReadOnlyList<Dictionary<string, object>> records,
        IReadOnlyList<int>? rowNumbers,
        bool continueOnError,
        bool dryRun,
        TimeSpan? loopLimit,
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        CancellationToken ct)
    {
        var batch = NewBatch(permissions, user, definition);

        // One transaction, each row written before the next is checked, so a lifecycle hook sees
        // the rows before it. Rolled back when a row is refused and the import is all or nothing.
        return runner.RunAsync(async (scope, requestToken) =>
        {
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            if (loopLimit is { } limit)
                limited.CancelAfter(limit);
            var token = limited.Token;

            var batchSession = scope.GetRequiredService<IDocumentSession>();
            var creator = scope.GetRequiredService<IContentCreator>();
            var rowErrors = new List<Response.RowError>();
            var written = 0;

            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i] ?? new Dictionary<string, object>();
                ReadAmounts(definition, record);
                var request = new ContentCreateRequest { ContentType = contentType, Data = record, Status = status };

                var error = await WriteRowAsync(creator, batchSession, request, user.Id, batch, httpContext, token);
                if (error is null)
                    written++;
                else
                    rowErrors.Add(new Response.RowError { Row = rowNumbers is null ? i : rowNumbers[i], Messages = error });
            }

            var commit = !dryRun && (rowErrors.Count == 0 || continueOnError);
            var created = rowErrors.Count == 0 || continueOnError ? written : 0;
            return new ContentBatchOutcome<BatchResult>(new BatchResult(created, rowErrors), commit);
        }, ct);
    }

    // Every record goes through the content create write path, the same as POST /api/contents:
    // a field the caller may not see is dropped, the record is validated, and the type's
    // lifecycle hooks run. The batch carries the definition, and what earlier rows claimed, so
    // a singleton type takes one row and two rows cannot share a slug. Admit asks whether the
    // create rule holds for each row as it will be stored, before the batch counts the row.
    private static ContentCreateBatch NewBatch(IPermissionResolver permissions, User user, ContentTypeDefinition? definition)
    {
        var batch = new ContentCreateBatch
        {
            Admit = async (request, token) =>
            {
                var asStored = new Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = request.ContentType,
                    Data = new Dictionary<string, object>(request.Data, request.Data.Comparer),
                    Status = request.Status,
                    CreatedBy = user.Id,
                    LastModifiedBy = user.Id,
                };

                return await permissions.CanPerformActionAsync(user, request.ContentType, "create", asStored, token)
                    ? null
                    : "Your create permission on this content type does not cover this row.";
            },
        };
        if (definition is not null)
            batch.UseSchema(definition);

        return batch;
    }

    /// <summary>The messages when the row is refused, or null when it was written.</summary>
    private static async Task<List<string>?> WriteRowAsync(
        IContentCreator creator, IDocumentSession batchSession, ContentCreateRequest request, Guid userId,
        ContentCreateBatch batch, Microsoft.AspNetCore.Http.HttpContext httpContext, CancellationToken token)
    {
        var messages = await creator.CheckAsync(request, userId, httpContext, batch, token);
        if (messages.Count > 0)
        {
            // Whatever a hook staged for a row it then refused is not this batch's.
            batchSession.EjectAllPendingChanges();
            return messages.ToList();
        }

        try
        {
            await creator.StageAsync(request, userId, batch, token);
            await batchSession.SaveChangesAsync(token);
            return null;
        }
        catch (Exception ex) when (ex is ContentUniquenessException or ContentCreateRefusedException)
        {
            // A uniqueness rule or the type's numbering refused the row: another entry, stored or
            // an earlier row, holds its values, or the number was taken. A row error like any other.
            batchSession.EjectAllPendingChanges();
            return [ex.Message];
        }
    }

    // A spreadsheet cell arrives as text, and a money field that declares a currency takes a
    // number. Plain decimal text is read as one here, so the entry stores a number; anything else
    // stays text and the validator refuses it naming the field.
    private static void ReadAmounts(ContentTypeDefinition? definition, Dictionary<string, object> record)
    {
        if (definition is null)
            return;

        foreach (var key in record.Keys.ToList())
        {
            var field = definition.Fields.FirstOrDefault(
                f => f is not null && string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));

            if (field is not null && FieldTypeRegistry.TryReadAmountText(field, record[key], out var amount))
                record[key] = amount;
        }
    }
}
