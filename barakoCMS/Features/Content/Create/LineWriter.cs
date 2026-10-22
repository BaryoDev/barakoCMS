using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Features.Content.Create;

/// <summary>What writing a header with its lines came to.</summary>
internal sealed record LinesResult(List<Guid> LineIds, List<string> Errors, bool Forbidden = false);

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
        Func<ContentCreateRequest, Task<bool>> admit,
        CancellationToken ct) =>
        runner.RunAsync(async (scope, token) =>
        {
            var session = scope.GetRequiredService<IDocumentSession>();
            var creator = scope.GetRequiredService<IContentCreator>();
            var batch = new ContentCreateBatch();
            var errors = new List<string>();
            var lineIds = new List<Guid>();

            var forbidden = false;
            var headerResult = await WriteOneAsync(creator, session, header, userId, batch, httpContext, admit, token, null);
            errors.AddRange(headerResult.Errors);
            forbidden = headerResult.Forbidden;
            for (var i = 0; errors.Count == 0 && !forbidden && i < lines.Count; i++)
            {
                var lineResult = await WriteOneAsync(creator, session, lines[i], userId, batch, httpContext, admit, token, i + 1);
                errors.AddRange(lineResult.Errors);
                forbidden = lineResult.Forbidden;
                if (lineResult.Errors.Count == 0 && !forbidden)
                    lineIds.Add(lines[i].Id!.Value);
            }

            return new ContentBatchOutcome<LinesResult>(
                new LinesResult(lineIds, errors, forbidden), errors.Count == 0 && !forbidden && !dryRun);
        }, ct);

    private static async Task<(List<string> Errors, bool Forbidden)> WriteOneAsync(
        IContentCreator creator, IDocumentSession session, ContentCreateRequest request, Guid userId,
        ContentCreateBatch batch, Microsoft.AspNetCore.Http.HttpContext httpContext,
        Func<ContentCreateRequest, Task<bool>> admit, CancellationToken token, int? line)
    {
        var prefix = line is { } n ? $"Line {n}: " : string.Empty;
        var messages = await creator.CheckAsync(request, userId, httpContext, batch, token);
        if (messages.Count > 0)
        {
            session.EjectAllPendingChanges();
            return (messages.Select(m => prefix + m).ToList(), false);
        }

        // Asked after the hooks, on the entry as it will be stored, as POST /api/contents asks it.
        if (!await admit(request))
        {
            session.EjectAllPendingChanges();
            return ([], true);
        }

        try
        {
            await creator.StageAsync(request, userId, batch, token);
            await session.SaveChangesAsync(token);
            return ([], false);
        }
        catch (barakoCMS.Core.Interfaces.ContentUniquenessException ex)
        {
            session.EjectAllPendingChanges();
            return ([prefix + ex.Message], false);
        }
    }

    /// <summary>
    /// The refusal when the header's total does not equal the sum of its lines' amounts, or null
    /// when the type declares no such rule or the totals agree.
    /// </summary>
    public static string? TotalMismatch(LinesDefinition lines, IDictionary<string, object>? header, IEnumerable<ContentCreateRequest> entries)
    {
        if (lines.TotalField is null || lines.LineAmountField is null) return null;

        var total = Read(header, lines.TotalField);
        var sum = 0m;
        foreach (var entry in entries) sum += Read(entry.Data, lines.LineAmountField) ?? 0m;
        return total == sum
            ? null
            : $"'{lines.TotalField}' is {total?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}, but the lines add up to {sum.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
    }

    private static decimal? Read(IDictionary<string, object>? data, string field)
    {
        var hit = data?.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase));
        var text = hit?.Value?.ToString();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
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
