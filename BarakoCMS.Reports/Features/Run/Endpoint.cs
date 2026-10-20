using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Reports.Features.Run;

internal sealed class Request
{
    public string Name { get; set; } = string.Empty;

    /// <summary>First day included, on the date field the report names (the day created by default).</summary>
    public DateOnly? From { get; set; }

    /// <summary>Last day included.</summary>
    public DateOnly? To { get; set; }

    /// <summary><c>csv</c> for a CSV download. Anything else, or nothing, is JSON.</summary>
    public string? Format { get; set; }
}

internal sealed class Response
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string? GroupBy { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    /// <summary>The names the figures in each row go by, in order.</summary>
    public List<string> Measures { get; set; } = [];

    public List<Row> Rows { get; set; } = [];

    /// <summary>Over every matching entry, not only the groups returned.</summary>
    public Row Totals { get; set; } = new();

    /// <summary>True when more groups matched than the report's group limit, so Rows is the first of them.</summary>
    public bool Truncated { get; set; }
}

internal sealed class Row
{
    public string? Key { get; set; }
    public long Count { get; set; }
    public Dictionary<string, decimal?> Values { get; set; } = [];
}

/// <summary>
/// GET /api/reports/{name}?from=&amp;to=&amp;format=. Runs a saved report as the caller.
/// </summary>
/// <remarks>
/// The caller needs no capability beyond being signed in, and sees only what their own read rules
/// show: their row conditions narrow the entries counted, and a field they cannot read cannot be the
/// group, a measure or a filter (403). A role whose rules cannot be written as a database condition
/// is refused rather than answered by loading every entry.
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    IPermissionResolver permissions,
    ISensitivityService sensitivity) : Endpoint<Request>
{
    public override void Configure()
    {
        Get("/api/reports/{name}");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var report = await session.LoadAsync<ReportDefinition>(req.Name, ct);
        if (report is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.From is { } from && req.To is { } to && to < from)
        {
            AddError("to", "'to' is before 'from'.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var type = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == report.ContentType, ct);
        if (type is null)
        {
            AddError("The content type of this report no longer exists.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var (plan, refusal) = await ReportPlanner.PlanAsync(report, type, user, User, permissions, sensitivity, ct);
        if (refusal is not null)
        {
            AddError(refusal.Message);
            await Send.ErrorsAsync(refusal.Status, ct);
            return;
        }

        var result = await ReportQuery.RunAsync(session, plan!, req.From, req.To, ct);
        var labels = plan!.Measures.Select(m => m.Measure.Label).ToList();

        if (string.Equals(req.Format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{report.Name}.csv\"";
            HttpContext.Response.Headers["X-Report-Truncated"] = result.Truncated ? "true" : "false";
            await Send.StringAsync(
                ReportCsv.Write(plan.GroupField ?? "group", labels, result), 200, "text/csv; charset=utf-8", ct);
            return;
        }

        Row ToRow(ReportRow r) => new() { Key = r.Key, Count = r.Count, Values = r.Measures };

        await Send.ResponseAsync(new Response
        {
            Name = report.Name,
            Title = report.Title,
            ContentType = report.ContentType,
            GroupBy = plan.GroupField,
            From = req.From,
            To = req.To,
            Measures = labels,
            Rows = result.Rows.Select(ToRow).ToList(),
            Totals = ToRow(result.Totals),
            Truncated = result.Truncated,
        }, cancellation: ct);
    }
}
