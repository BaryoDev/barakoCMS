using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.ContentType.SetFieldCurrency;

/// <summary>
/// PUT /api/content-types/{name}/fields/{field}/currency, which declares, changes or clears the
/// currency of one money field.
/// </summary>
/// <remarks>
/// Its own endpoint for the reason <c>SetFieldOptions</c> is: there is no general field update. It
/// is how a money field stored before currencies existed gets one, with no pass over its entries.
///
/// No entry is rewritten. An amount that already fits the scale is untouched and is now read in the
/// declared currency. Entries holding an amount with more decimal places than the scale, or a value
/// that is not a number, are counted, and the change is refused unless <c>force</c> is set: each of
/// them is refused on its next save until the amount is corrected. Changing from one code to
/// another on a field that entries hold amounts in is refused the same way, because nothing is
/// converted and every stored amount would be read under the new code.
///
/// Only the currency and scale are validated, not the whole type and not the field's name, so a
/// type created before some later rule existed can still declare one.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    // The field's value by key without regard to case, as the entry validator reads it. A number is
    // compared as numeric, never as a float. Text and anything else that is not a number or null
    // counts, since a write refuses it too.
    private const string NotFittingSql =
        "EXISTS (SELECT 1 FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) "
        + "AND CASE jsonb_typeof(e.value) "
        + "WHEN 'number' THEN round((e.value #>> '{}')::numeric, ?) <> (e.value #>> '{}')::numeric "
        + "WHEN 'null' THEN false ELSE true END)";

    private const string HoldingSql =
        "EXISTS (SELECT 1 FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) "
        + "AND jsonb_typeof(e.value) <> 'null')";

    public override void Configure()
    {
        Put("/api/content-types/{name}/fields/{field}/currency");
        // Changing what a field accepts is modelling, the same gate as adding the field.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        var fieldName = Route<string>("field") ?? string.Empty;

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        var field = def?.Fields.FirstOrDefault(
            f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        if (def is null || field is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!MoneyFields.IsMoney(field.Type))
        {
            AddError($"'{field.Name}' is of type '{field.Type}', and only a money field has a currency.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var proposed = new FieldDefinition
        {
            Name = field.Name,
            DisplayName = field.DisplayName,
            Type = field.Type,
            Currency = req.Currency,
            Scale = req.Scale,
        };

        var errors = MoneyFields.DefinitionErrors(proposed);
        if (errors.Count > 0)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var declares = MoneyFields.TryResolve(proposed, out var currency, out var scale);

        if (string.Equals(field.Currency, req.Currency, StringComparison.Ordinal) && field.Scale == req.Scale)
        {
            await Send.OkAsync(Answer(def, field, declares, currency, scale, 0, 0), ct);
            return;
        }

        var notFitting = declares
            ? await CountAsync(def.Name, NotFittingSql, [field.Name, scale], ct)
            : 0;

        var relabelled = declares && field.Currency is not null
                         && !string.Equals(field.Currency, currency, StringComparison.Ordinal)
            ? await CountAsync(def.Name, HoldingSql, [field.Name], ct)
            : 0;

        if ((notFitting > 0 || relabelled > 0) && !req.Force)
        {
            if (notFitting > 0)
            {
                AddError(
                    $"{notFitting} {(notFitting == 1 ? "entry holds" : "entries hold")} a value in "
                    + $"'{field.Name}' with more than {scale} decimal {(scale == 1 ? "place" : "places")}, "
                    + "or one that is not a number. They are left as they are, and each is refused on "
                    + "its next save until the amount is corrected.");
            }

            if (relabelled > 0)
            {
                AddError(
                    $"{relabelled} {(relabelled == 1 ? "entry holds" : "entries hold")} an amount in "
                    + $"'{field.Name}' stored as {field.Currency}. Nothing is converted, so each would "
                    + $"be read as {currency}.");
            }

            AddError("Resend with force set to true to go ahead anyway.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var before = Describe(field.Currency, field.Scale);
        field.Currency = req.Currency;
        field.Scale = req.Scale;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            "contenttype.field.currency.changed",
            actorId,
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["field"] = field.Name,
                ["from"] = before,
                ["to"] = Describe(field.Currency, field.Scale),
                ["entriesNotFitting"] = notFitting,
                ["entriesRelabelled"] = relabelled,
            },
            ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.OkAsync(Answer(def, field, declares, currency, scale, notFitting, relabelled), ct);
    }

    private static Response Answer(
        ContentTypeDefinition def, FieldDefinition field, bool declares, string currency, int scale,
        int notFitting, int relabelled) => new()
    {
        Name = def.Name,
        Field = field.Name,
        Currency = declares ? currency : null,
        Scale = declares ? scale : null,
        EntriesNotFitting = notFitting,
        EntriesRelabelled = relabelled,
    };

    private static string Describe(string? currency, int? scale) =>
        currency is null ? "none" : scale is null ? currency : $"{currency} at {scale} decimal places";

    /// <summary>Entries of the type, of any status, that the fragment matches.</summary>
    private async Task<int> CountAsync(string type, string sql, object[] parameters, CancellationToken ct) =>
        await session.Query<ContentDoc>()
            .Where(c => c.ContentType == type && c.MatchesSql(sql, parameters))
            .CountAsync(ct);
}
