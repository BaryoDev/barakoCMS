using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.ContentType.SetFieldOptions;

/// <summary>
/// PUT /api/content-types/{name}/fields/{field}/options, which replaces the options of one choice
/// field.
/// </summary>
/// <remarks>
/// Its own endpoint for the reason <c>SetFieldSensitivity</c> is: there is no general field update,
/// because a field's name, type and target are load bearing once entries exist. Options are the one
/// part of a choice that has to keep changing after it is in use. A size sells out, a label is
/// reworded, a new area of focus is added.
///
/// Rewording a label or adding an option changes no entry, because entries store the value. Removing
/// an option that entries still hold is refused unless <c>force</c> is set, with the count of entries
/// affected. Those entries are left as they are, since rewriting them would mean choosing a
/// replacement on the client's behalf.
///
/// Only this field is validated, not the whole type, so a type created before some later rule
/// existed can still have its options changed.
/// </remarks>
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IContentTypeValidatorService _validator;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _validator = validator;
        _openApiCache = openApiCache;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Put("/api/content-types/{name}/fields/{field}/options");
        // Changing what a field accepts is modelling, the same gate as adding the field.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        var fieldName = Route<string>("field") ?? string.Empty;

        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        var field = def?.Fields.FirstOrDefault(
            f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        if (def is null || field is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!string.Equals(field.Type, "choice", StringComparison.OrdinalIgnoreCase))
        {
            AddError($"'{field.Name}' is of type '{field.Type}', and only a choice field has options.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var options = req.Options ?? new List<FieldOption>();
        var proposed = new FieldDefinition
        {
            Name = field.Name,
            DisplayName = field.DisplayName,
            Type = field.Type,
            Options = options,
            Multiple = field.Multiple,
        };

        var (valid, errors) = _validator.Validate(def.Name, def.DisplayName, [proposed]);
        if (!valid)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Exact, the way entries are matched. Re-casing a value is a removal and an addition.
        var removed = (field.Options ?? new List<FieldOption>())
            .Select(o => o.Value)
            .Where(v => !options.Any(o => string.Equals(o.Value, v, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var holding = removed.Count == 0 ? 0 : await CountHoldingAsync(def.Name, field, removed, ct);

        if (holding > 0 && !req.Force)
        {
            AddError(
                $"{holding} {(holding == 1 ? "entry holds" : "entries hold")} "
                + string.Join(", ", removed.Select(v => $"'{v}'"))
                + $" in '{field.Name}'. Removing {(removed.Count == 1 ? "it" : "them")} leaves those entries "
                + "with a value the field no longer accepts, and each is refused on its next save until "
                + "a value still offered is picked. Resend with force set to true to remove anyway.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var before = field.Options?.Count ?? 0;
        field.Options = options;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        _session.Store(def);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(
            _session,
            _tenant.Slug,
            "contenttype.field.options.changed",
            actorId,
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["field"] = field.Name,
                ["optionsBefore"] = before,
                ["optionsAfter"] = options.Count,
                ["removed"] = string.Join(",", removed),
                ["entriesHoldingRemoved"] = holding,
            },
            ct: ct);

        await _session.SaveChangesAsync(ct);

        // The OpenAPI document lists the options as an enum.
        _openApiCache.Invalidate(_tenant.Slug);

        await Send.OkAsync(new Response
        {
            Name = def.Name,
            Field = field.Name,
            Options = options,
            Removed = removed,
            EntriesHoldingRemoved = holding,
        }, ct);
    }

    /// <summary>Entries of any status holding at least one of the values, counted once each.</summary>
    /// <remarks>
    /// Drafts and archived entries count, because each of them is refused on its next save just the
    /// same. The match is the delivery filter's, so "holds" means the same thing here as it does to a
    /// caller filtering by the value.
    /// </remarks>
    private async Task<int> CountHoldingAsync(string type, FieldDefinition field, List<string> values, CancellationToken ct)
    {
        var sql = new List<string>();
        var parameters = new List<object>();
        foreach (var value in values)
        {
            var (fragment, bound) = DeliveryQuery.ToSql(
                new DeliveryFilter(field.Name, FilterOp.Eq, value, field.Type, field.Multiple));
            sql.Add($"COALESCE({fragment}, false)");
            parameters.AddRange(bound);
        }

        return await _session.Query<ContentDoc>()
            .Where(c => c.ContentType == type && c.MatchesSql(string.Join(" OR ", sql), parameters.ToArray()))
            .CountAsync(ct);
    }
}
