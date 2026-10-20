using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Features.Content.Create;

/// <summary>What writing a header with its lines came to.</summary>
internal sealed record LinesResult(List<Guid> LineIds, List<string> Errors);

/// <summary>
/// Writes a header and its lines through the content create path in one transaction, the same
/// runner the bulk import uses. Every entry is checked and staged the way POST /api/contents does,
/// each is flushed before the next is checked, and the commit is all or nothing.
/// </summary>
internal static class LineWriter
{
    public static Task<LinesResult> WriteAsync(
        IContentBatchRunner runner,
        ContentCreateRequest header,
        IReadOnlyList<ContentCreateRequest> lines,
        Guid userId,
        bool dryRun,
        Microsoft.AspNetCore.Http.HttpContext httpContext,
        CancellationToken ct) =>
        runner.RunAsync(async (scope, token) =>
        {
            var session = scope.GetRequiredService<IDocumentSession>();
            var creator = scope.GetRequiredService<IContentCreator>();
            var batch = new ContentCreateBatch();
            var errors = new List<string>();
            var lineIds = new List<Guid>();

            errors.AddRange(await WriteOneAsync(creator, session, header, userId, batch, httpContext, token, null));
            for (var i = 0; errors.Count == 0 && i < lines.Count; i++)
            {
                var lineErrors = await WriteOneAsync(creator, session, lines[i], userId, batch, httpContext, token, i + 1);
                errors.AddRange(lineErrors);
                if (lineErrors.Count == 0)
                    lineIds.Add(lines[i].Id!.Value);
            }

            return new ContentBatchOutcome<LinesResult>(new LinesResult(lineIds, errors), errors.Count == 0 && !dryRun);
        }, ct);

    private static async Task<List<string>> WriteOneAsync(
        IContentCreator creator, IDocumentSession session, ContentCreateRequest request, Guid userId,
        ContentCreateBatch batch, Microsoft.AspNetCore.Http.HttpContext httpContext, CancellationToken token, int? line)
    {
        var prefix = line is { } n ? $"Line {n}: " : string.Empty;
        var messages = await creator.CheckAsync(request, userId, httpContext, batch, token);
        if (messages.Count > 0)
        {
            session.EjectAllPendingChanges();
            return messages.Select(m => prefix + m).ToList();
        }

        try
        {
            await creator.StageAsync(request, userId, batch, token);
            await session.SaveChangesAsync(token);
            return [];
        }
        catch (barakoCMS.Core.Interfaces.ContentUniquenessException ex)
        {
            session.EjectAllPendingChanges();
            return [prefix + ex.Message];
        }
    }

    /// <summary>A line request with the parent field set to the header, whatever the caller sent there.</summary>
    public static ContentCreateRequest Line(
        LinesDefinition lines, Dictionary<string, object> data, Guid headerId, ContentStatus status, SensitivityLevel sensitivity)
    {
        var copy = new Dictionary<string, object>(data ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        copy[lines.ParentField] = headerId.ToString();
        return new ContentCreateRequest
        {
            ContentType = lines.ContentType,
            Data = copy,
            Status = status,
            Sensitivity = sensitivity,
            Id = Guid.NewGuid(),
        };
    }
}
