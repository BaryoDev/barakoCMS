using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Reports;

/// <summary>A run the caller may not make, or could not be made as saved.</summary>
internal sealed record ReportRefusal(int Status, string Message);

/// <summary>
/// Turns a saved definition into a plan for one caller. This is where access follows the reader:
/// the caller's read rules become the row condition, and a field the caller cannot read cannot be
/// grouped on, measured or filtered on, since the totals would give its values away.
/// </summary>
internal static class ReportPlanner
{
    public static async Task<(ReportPlan? Plan, ReportRefusal? Refusal)> PlanAsync(
        ReportDefinition report,
        ContentTypeDefinition type,
        User user,
        ClaimsPrincipal principal,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        CancellationToken ct)
    {
        var predicate = await permissions.ReadPredicateAsync(user, type.Name, ct);
        if (predicate.Sql is null)
        {
            return (null, new(403, "A report cannot be computed under this role's read rules for the content type."));
        }

        if (predicate.Sql == ReadPredicate.Nothing.Sql)
        {
            return (null, new(403, "This role cannot read the content type."));
        }

        var needed = new List<string>();
        if (report.GroupBy is not null) needed.Add(report.GroupBy);
        needed.AddRange(report.Measures.Where(m => m.Fn != "count" && m.Field is not null).Select(m => m.Field!));
        needed.AddRange(report.Filters.Select(f => f.Field));
        if (report.DateField is not null) needed.Add(report.DateField);

        var resolved = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in needed.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = type.Fields.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                return (null, new(400, $"The report uses '{name}', which the content type does not declare."));
            }

            foreach (var field in matches)
            {
                if (string.Equals(field.Type, "token", StringComparison.OrdinalIgnoreCase)
                    || !await sensitivity.MayReadFieldAsync(type.Name, field, null, principal, ct))
                {
                    return (null, new(403, $"This role cannot read '{name}', so it cannot be used in a report."));
                }
            }

            resolved[name] = matches[0];
        }

        foreach (var measure in report.Measures.Where(m => m.Fn != "count"))
        {
            if (!FieldTypeRegistry.IsNumericType(resolved[measure.Field!].Type))
            {
                return (null, new(400, $"'{measure.Field}' is not a numeric field, so '{measure.Fn}' does not apply to it."));
            }
        }

        var withheld = new List<string>();
        foreach (var level in new[] { SensitivityLevel.Sensitive, SensitivityLevel.Hidden })
        {
            if (await sensitivity.MaySeeDocumentAsync(level, principal, ct)) continue;
            withheld.Add(((int)level).ToString(System.Globalization.CultureInfo.InvariantCulture));
            withheld.Add(level.ToString());
        }

        var measures = report.Measures.Count == 0
            ? [new ReportMeasure { Fn = "count" }]
            : report.Measures;

        return (new ReportPlan
        {
            ContentType = type.Name,
            GroupField = report.GroupBy is null ? null : resolved[report.GroupBy].Name,
            Measures = measures.Select(m => (m, m.Fn == "count" ? (string?)null : resolved[m.Field!].Name)).ToList(),
            Filters = report.Filters.Select(f => (f, resolved[f.Field])).ToList(),
            DateField = report.DateField is null ? null : resolved[report.DateField].Name,
            GroupLimit = Math.Clamp(report.GroupLimit, 1, ReportDefinition.MaxGroupLimit),
            AccessSql = predicate.Sql,
            AccessParameters = predicate.Parameters,
            WithheldSensitivity = withheld.ToArray(),
        }, null);
    }
}
