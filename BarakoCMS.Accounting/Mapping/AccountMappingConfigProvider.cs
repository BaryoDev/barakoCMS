using System.Text.Json;
using barakoCMS.Core.Interfaces;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// The <c>accountingMappings</c> section: each mapping of the tenant under its name.
/// </summary>
/// <remarks>
/// A mapping is checked against the chart as it stands, so an account a mapping names has to exist
/// before the mapping does. A mapping the document leaves out is deleted, destructively.
/// </remarks>
internal sealed class AccountMappingConfigProvider(IDocumentSession session) : IConfigProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Section => "accountingMappings";

    public int Order => 450;

    public string ReadCapability => AccountingCapabilities.ManageMappings;

    public string WriteCapability => AccountingCapabilities.ManageMappings;

    public async Task<ConfigSectionExport?> ExportAsync(ConfigCaller caller, CancellationToken ct)
    {
        var stored = await session.Query<AccountMapping>().ToListAsync(ct);
        return new ConfigSectionExport
        {
            Documents = stored.ToDictionary(
                m => m.Name, m => JsonSerializer.SerializeToElement(m.Rule, Json), StringComparer.Ordinal),
        };
    }

    public async Task<ConfigSectionPlan> PlanAsync(
        IReadOnlyDictionary<string, JsonElement> desired, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();
        var changes = new List<ConfigChange>();
        var stored = (await session.Query<AccountMapping>().ToListAsync(ct)).ToDictionary(m => m.Name, StringComparer.Ordinal);

        foreach (var (name, document) in desired.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (await ReadAsync(name, document, errors, ct) is not { } rule)
            {
                continue;
            }

            var normal = JsonSerializer.SerializeToElement(rule, Json);
            if (!stored.TryGetValue(name, out var current))
            {
                changes.Add(new ConfigChange { Key = name, Kind = ConfigChangeKind.Create, Desired = normal, Summary = $"add mapping '{name}'" });
            }
            else if (JsonSerializer.Serialize(current.Rule, Json) != normal.GetRawText())
            {
                changes.Add(new ConfigChange { Key = name, Kind = ConfigChangeKind.Change, Desired = normal, Summary = $"change mapping '{name}'" });
            }
        }

        changes.AddRange(stored.Keys.Where(name => !desired.ContainsKey(name)).Order(StringComparer.Ordinal).Select(name => new ConfigChange
        {
            Key = name, Kind = ConfigChangeKind.Destructive, Summary = $"delete mapping '{name}'",
        }));

        return new ConfigSectionPlan { Section = Section, Changes = changes, Errors = errors };
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ConfigSectionPlan plan, ConfigCaller caller, CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var change in plan.Changes)
        {
            if (change.Desired is not { } document)
            {
                session.Delete<AccountMapping>(change.Key);
                continue;
            }

            if (await ReadAsync(change.Key, document, errors, ct) is { } rule)
            {
                session.Store(new AccountMapping { Name = change.Key, Rule = rule });
            }
        }

        return errors;
    }

    /// <summary>The document as a rule with its sides in lower case and its accounts spelled as the chart spells them, or null with the reasons in <paramref name="errors"/>.</summary>
    private async Task<MappingRule?> ReadAsync(string name, JsonElement document, List<string> errors, CancellationToken ct)
    {
        MappingRule? rule;
        try
        {
            rule = document.ValueKind == JsonValueKind.Object ? document.Deserialize<MappingRule>(Json) : null;
        }
        catch (JsonException ex)
        {
            errors.Add($"'{name}' cannot be read: {ex.Message}");
            return null;
        }

        if (rule?.Lines is not { Count: > 0 } lines)
        {
            errors.Add($"'{name}': a mapping needs at least one line.");
            return null;
        }

        var problems = new List<string>();
        var chart = new AccountService(session);
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var codes = lines.Select(l => l.Account).Concat(rule.AccountByField?.Values ?? Enumerable.Empty<string>());
        foreach (var code in codes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await chart.GetByCodeAsync(code, ct) is not { } account)
            {
                problems.Add($"account '{code}' is not in the chart.");
            }
            else
            {
                canonical[code] = account.Code;
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Account))
            {
                problems.Add($"line {i + 1} has no account.");
            }

            if (!string.Equals(line.Side, "debit", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(line.Side, "credit", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"line {i + 1}: side must be debit or credit.");
            }

            if (string.IsNullOrWhiteSpace(line.Amount))
            {
                problems.Add($"line {i + 1} has no amount.");
            }
        }

        errors.AddRange(problems.Select(p => $"'{name}': {p}"));
        return problems.Count > 0
            ? null
            : rule with
            {
                Lines = [.. lines.Select(l => l with { Account = canonical.GetValueOrDefault(l.Account, l.Account), Side = l.Side.ToLowerInvariant() })],
                AccountByField = rule.AccountByField?.ToDictionary(f => f.Key, f => canonical.GetValueOrDefault(f.Value, f.Value)),
            };
    }
}
