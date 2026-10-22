using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Numbering;

/// <summary>A type's numbering rule as its document.</summary>
internal sealed record NumberingConfig(
    string Field,
    string? Prefix,
    int Padding,
    NumberReset Reset,
    NumberStrategy Strategy,
    string? TimeZoneId = null,
    string? SeriesTemplate = null,
    bool ClientAllocated = false);

/// <summary>
/// The <c>numbering</c> section: the numbering rule of each content type that has one, under the
/// type's name.
/// </summary>
/// <remarks>
/// A rule is checked the way <c>PUT /api/content-types/{name}/numbering</c> checks it. A document
/// names only the types it sets. A type the document leaves out keeps its rule, so this section
/// never removes one. To take a rule off a type, use that route.
/// </remarks>
internal sealed class NumberingConfigProvider(IDocumentSession session, TenantContext tenant) : IConfigProvider
{
    public string Section => "numbering";

    public int Order => 150;

    public string ReadCapability => SystemCapabilities.ManageContentTypes;

    public string WriteCapability => SystemCapabilities.ManageContentTypes;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in await session.Query<ContentTypeDefinition>().ToListAsync(ct))
        {
            if (definition.Numbering is { } rule)
            {
                documents[definition.Name] = ConfigJson.ToElement(Of(rule));
            }
        }

        return new ConfigSectionExport { Documents = documents };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();

        foreach (var (name, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (await ResolveAsync(name, document, errors, ct) is not var (definition, rule))
            {
                continue;
            }

            if (definition.Numbering is null)
            {
                changes.Add(new ConfigChange
                {
                    Key = name, Kind = ConfigChangeKind.Create, Desired = document, Summary = $"set numbering on '{definition.Name}'",
                });
            }
            else if (!ConfigJson.Same(Of(definition.Numbering), Of(rule)))
            {
                changes.Add(new ConfigChange
                {
                    Key = name, Kind = ConfigChangeKind.Change, Desired = document, Summary = $"change numbering on '{definition.Name}'",
                });
            }
        }

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var principal = caller.Principal;
        var actor = Guid.TryParse(principal?.FindFirst("UserId")?.Value, out var id) ? id : (Guid?)null;

        foreach (var change in plan.Changes)
        {
            if (change.Desired is not { } document
                || await ResolveAsync(change.Key, document, errors, ct) is not var (definition, rule))
            {
                continue;
            }

            definition.Numbering = rule;
            definition.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(definition);
            await AuditLog.RecordAsync(session, tenant.Slug, "contenttype.numbering_set", actor, principal?.FindFirst("Username")?.Value,
                targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
                metadata: new Dictionary<string, object>
                {
                    ["field"] = rule.Field,
                    ["strategy"] = rule.Strategy.ToString(),
                }, ct: ct);
        }

        return errors;
    }

    private static NumberingConfig Of(NumberingRule rule) =>
        new(rule.Field, rule.Prefix, rule.Padding, rule.Reset, rule.Strategy, rule.TimeZoneId, rule.SeriesTemplate, rule.ClientAllocated);

    /// <summary>The type a key names and the checked rule its document sets, or null with the reasons added to <paramref name="errors"/>.</summary>
    private async Task<(ContentTypeDefinition Definition, NumberingRule Rule)?> ResolveAsync(
        string name, JsonElement document, List<string> errors, CancellationToken ct)
    {
        if (ConfigJson.Read<NumberingConfig>(name, document, errors) is not { } config)
        {
            return null;
        }

        var lowered = name.ToLower();
        if (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct) is not { } definition)
        {
            errors.Add($"'{name}': there is no content type with that name.");
            return null;
        }

        var rule = new NumberingRule
        {
            Field = config.Field?.Trim() ?? string.Empty, Prefix = config.Prefix ?? string.Empty, Padding = config.Padding,
            Reset = config.Reset, TimeZoneId = config.TimeZoneId, Strategy = config.Strategy,
            SeriesTemplate = config.SeriesTemplate, ClientAllocated = config.ClientAllocated,
        };

        var problems = NumberingRules.DefinitionErrors(definition, rule);
        if (problems.Count > 0)
        {
            errors.AddRange(problems.Select(p => $"'{name}': {p}"));
            return null;
        }

        rule.Field = definition.Fields.First(f => string.Equals(f.Name, rule.Field, StringComparison.OrdinalIgnoreCase)).Name;
        return (definition, rule);
    }
}
