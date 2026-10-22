using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Reports.Features.Create;

/// <summary>
/// POST /api/reports. Saves a report definition for the tenant.
/// </summary>
/// <remarks>
/// Checks the definition against the content type's schema, so a report that names a field the type
/// does not have, or sums one that is not a number, is refused when it is saved and not on every
/// run. Whether a given caller may read those fields is decided at run time, by the caller.
/// </remarks>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/reports");
        Definition.RequireCapability(ReportsCapabilities.ManageReports, ReportsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var type = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == req.ContentType, ct);
        if (type is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await session.LoadAsync<ReportDefinition>(req.Name, ct) is not null)
        {
            AddError(r => r.Name, "A report with this name exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var report = new ReportDefinition
        {
            Name = req.Name,
            Title = req.Title,
            ContentType = type.Name,
            Filters = req.Filters,
            GroupBy = req.GroupBy,
            Measures = req.Measures.Count == 0 ? [new ReportMeasure { Fn = "count" }] : req.Measures,
            DateField = req.DateField,
            GroupLimit = req.GroupLimit ?? ReportDefinition.DefaultGroupLimit,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId) ? userId : Guid.Empty,
        };

        foreach (var error in Problems(report, type))
        {
            ValidationFailures.Add(new ValidationFailure(error.Property, error.Message));
        }

        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        session.Store(report);
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(Response.From(report), 201, ct);
    }

    private static IEnumerable<(string Property, string Message)> Problems(ReportDefinition report, ContentTypeDefinition type)
    {
        FieldDefinition? Find(string name) =>
            type.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (report.GroupBy is not null && Find(report.GroupBy) is null)
            yield return ("groupBy", $"'{report.GroupBy}' is not a field of {type.Name}.");

        if (report.DateField is not null && Find(report.DateField) is null)
            yield return ("dateField", $"'{report.DateField}' is not a field of {type.Name}.");

        foreach (var filter in report.Filters)
        {
            var field = Find(filter.Field);
            if (field is null)
            {
                yield return ("filters", $"'{filter.Field}' is not a field of {type.Name}.");
            }
            else if (barakoCMS.Core.Validation.FieldTypeRegistry.IsNumericType(field.Type)
                     && !decimal.TryParse(filter.Value, System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                yield return ("filters", $"'{filter.Value}' is not a number, and '{filter.Field}' is numeric.");
            }
            else if (string.Equals(field.Type, "bool", StringComparison.OrdinalIgnoreCase)
                     && !bool.TryParse(filter.Value, out _))
            {
                yield return ("filters", $"'{filter.Value}' is not true or false, and '{filter.Field}' is a bool.");
            }
        }

        foreach (var measure in report.Measures.Where(m => m.Fn != "count"))
        {
            var field = Find(measure.Field!);
            if (field is null)
                yield return ("measures", $"'{measure.Field}' is not a field of {type.Name}.");
            else if (!barakoCMS.Core.Validation.FieldTypeRegistry.IsNumericType(field.Type))
                yield return ("measures", $"'{measure.Field}' is not a numeric field, so {measure.Fn} does not apply to it.");
        }
    }
}
