using System.Text.Json;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Migrations;

/// <summary>
/// The stored texts that hold the literal <c>{{=</c>. Before expression holes existed they were sent
/// as written. After, they evaluate. <c>db-migrate</c> lists them so an operator can look first.
/// </summary>
/// <remarks>
/// Reads email templates and layouts, and the parameters of every workflow, across all tenants.
/// A line names where the text is and never what it says.
/// </remarks>
internal static class ExpressionTemplates
{
    public static async Task<IReadOnlyList<string>> ListAsync(IDocumentStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var lines = new List<string>();
        if (!await HasTablesAsync(store, ct)) return lines;

        await using var session = store.QuerySession();

        foreach (var type in new[] { EmailTemplateRenderer.TemplateType, EmailTemplateRenderer.LayoutType })
        {
            var entries = await session.Query<Content>()
                .Where(c => c.AnyTenant() && c.ContentType == type)
                .ToListAsync(ct);
            foreach (var entry in entries.OrderBy(e => e.Id))
            {
                foreach (var (field, value) in entry.Data ?? [])
                {
                    if (Holds(value)) lines.Add($"{type} {entry.Id}, field {field}");
                }
            }
        }

        var workflows = await session.Query<WorkflowDefinition>().Where(w => w.AnyTenant()).ToListAsync(ct);
        foreach (var workflow in workflows.OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            var count = WorkflowSaveRights.Flatten(workflow)
                .SelectMany(a => a.Parameters)
                .Count(p => Holds(p.Value));
            if (count > 0) lines.Add($"workflow '{workflow.Name}', {count} parameter(s)");
        }

        return lines;
    }

    /// <summary>
    /// Whether the tables for entries and workflows exist. A query on a table that is missing would
    /// create it, and a status run changes nothing.
    /// </summary>
    private static async Task<bool> HasTablesAsync(IDocumentStore store, CancellationToken ct)
    {
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_regclass('public.mt_doc_contents') is not null and to_regclass('public.mt_doc_workflowdefinition') is not null";
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>The deepest a value is followed through JSON held inside text. Deeper is listed, since it cannot be ruled out.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Whether a stored value holds an expression hole, read the way the renderer reads it: by the
    /// shared scanner, over the decoded text. A value written with JSON escapes decodes to the same
    /// text, and JSON held inside a parameter is read too. A value that cannot be read in full is listed.
    /// </summary>
    internal static bool Holds(object? value, int depth = 0)
    {
        if (depth > MaxDepth) return true;

        switch (value)
        {
            case null:
                return false;
            case string text:
                return HoldsText(text, depth);
            case JsonElement element:
                return HoldsElement(element, depth);
            case System.Collections.IDictionary map:
                return map.Values.Cast<object?>().Any(v => Holds(v, depth + 1));
            case System.Collections.IEnumerable list:
                return list.Cast<object?>().Any(v => Holds(v, depth + 1));
            default:
                return false;
        }
    }

    private static bool HoldsElement(JsonElement element, int depth) => element.ValueKind switch
    {
        JsonValueKind.String => HoldsText(element.GetString(), depth),
        JsonValueKind.Object => element.EnumerateObject().Any(p => HoldsElement(p.Value, depth + 1)),
        JsonValueKind.Array => element.EnumerateArray().Any(e => HoldsElement(e, depth + 1)),
        _ => false,
    };

    private static bool HoldsText(string? text, int depth)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            if (TemplateHoles.Scan(text).Any(h => h.Form == HoleForm.Expression)) return true;
        }
        catch (TemplateScanException)
        {
            return true;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[')) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            return HoldsElement(document.RootElement, depth + 1);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
