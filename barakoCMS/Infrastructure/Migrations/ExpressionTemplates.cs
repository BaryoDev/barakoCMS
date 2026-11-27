using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex Opening = new(@"\{\{\s*=", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static async Task<IReadOnlyList<string>> ListAsync(IDocumentStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var lines = new List<string>();
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
                    if (Holds(AsText(value))) lines.Add($"{type} {entry.Id}, field {field}");
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

    /// <summary>Whether a text holds the literal. A text that cannot be read in full is listed, since it cannot be ruled out.</summary>
    internal static bool Holds(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > TemplateHoles.MaxScanLength) return true;

        try
        {
            return Opening.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => value.ToString(),
    };
}
