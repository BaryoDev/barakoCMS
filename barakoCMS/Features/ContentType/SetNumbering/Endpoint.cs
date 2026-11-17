using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using barakoCMS.Infrastructure.Jobs;
using Marten;

namespace barakoCMS.Features.ContentType.SetNumbering;

internal sealed class Request
{
    /// <summary>The text field that holds the number. Null or empty takes the rule off the type.</summary>
    public string? Field { get; set; }

    public string Prefix { get; set; } = string.Empty;
    public int Padding { get; set; } = 5;
    public NumberReset Reset { get; set; }
    public string? TimeZoneId { get; set; }
    public NumberStrategy Strategy { get; set; }

    /// <summary>Series name with <c>{branch}</c> and <c>{type}</c> tokens. Null keeps the type's own series.</summary>
    public string? SeriesTemplate { get; set; }

    /// <summary>True when a client may take numbers of this series from the next-number route.</summary>
    public bool ClientAllocated { get; set; }
}

internal sealed class Response
{
    public string ContentType { get; init; } = string.Empty;

    /// <summary>The rule now on the type, or null when it has none.</summary>
    public NumberingRule? Numbering { get; init; }

    /// <summary>What the next number would look like, for a rule that is set.</summary>
    public string? Example { get; init; }
}

/// <summary>PUT /api/content-types/{name}/numbering: sets or clears the rule that numbers a type's new entries.</summary>
/// <remarks>
/// The rule says which field takes the number and which strategy hands it out, so the cost of the
/// choice is visible where it is made (see <see cref="NumberStrategy"/>). Changing the strategy keeps
/// the series' count. Entries that already exist keep what they hold; only entries created from now
/// on are numbered.
/// </remarks>
internal sealed class Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/numbering");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var lowered = (Route<string>("name") ?? string.Empty).ToLower();
        if (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct) is not { } definition)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        NumberingRule? rule = null;
        if (!string.IsNullOrWhiteSpace(req.Field))
        {
            rule = new NumberingRule
            {
                Field = req.Field.Trim(), Prefix = req.Prefix ?? string.Empty, Padding = req.Padding,
                Reset = req.Reset, TimeZoneId = req.TimeZoneId, Strategy = req.Strategy,
                SeriesTemplate = string.IsNullOrWhiteSpace(req.SeriesTemplate) ? null : req.SeriesTemplate.Trim(),
                ClientAllocated = req.ClientAllocated,
            };
            foreach (var error in NumberingRules.DefinitionErrors(definition, rule))
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
            rule.Field = definition.Fields.First(f => string.Equals(f.Name, rule.Field, StringComparison.OrdinalIgnoreCase)).Name;
        }

        definition.Numbering = rule;
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(definition);

        var actor = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : (Guid?)null;
        await AuditLog.RecordAsync(session, tenant.Slug, "contenttype.numbering_set", actor, User.FindFirst("Username")?.Value,
            targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
            metadata: new Dictionary<string, object>
            {
                ["field"] = rule?.Field ?? string.Empty,
                ["strategy"] = rule?.Strategy.ToString() ?? string.Empty,
            }, ct: ct);
        await session.SaveChangesAsync(ct);

        // The example shows the number's form only, so a branch series is read with a placeholder branch.
        var series = rule is null ? null : NumberingRules.SeriesFor(definition, rule, NumberingRules.NeedsBranch(rule) ? "{branch}" : null);
        await Send.OkAsync(new Response
        {
            ContentType = definition.Name,
            Numbering = rule,
            Example = series?.Format(1, series.PeriodFor(DateTimeOffset.UtcNow)),
        }, ct);
    }
}
