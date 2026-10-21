using barakoCMS.Core.Interfaces;
using System.Globalization;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>The condition kinds that read other entries or a registered lookup.</summary>
internal sealed partial class RuleScan
{
    /// <summary>
    /// A field of the row, its name matched ignoring case the way the rest of the engine reads
    /// names, so a key stored as "phone" is not missed by a rule naming "Phone".
    /// </summary>
    private const string FieldValue =
        "(select e.value from jsonb_each_text(d.data -> 'Data') e where lower(e.key) = lower(?) order by e.key limit 1)";

    private static bool ValidName(string name) =>
        name.Length > 0 && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static string InvalidName(string? name) => $"Field '{name}' is not a valid field name.";

    /// <summary>Holds when no other entry of this type has the same value once both are normalised.</summary>
    private async Task<Held> UniqueAsync(RuleCondition c, CancellationToken ct)
    {
        var field = c.Field ?? "";
        if (!ValidName(field)) return new(false, InvalidName(field));

        var value = RuleFieldKinds.Text(ctx.Data, field) ?? "";
        if (value.Length == 0) return new(true, null);

        // Both sides go through the same SQL, so the stored and the incoming value are normalised
        // by one implementation. An incoming value that normalises to nothing clashes with nothing.
        var right = Normalised("?", c.Normalise);
        var sql = $"{Normalised(FieldValue, c.Normalise)} = {right} and {right} <> ''";
        var args = new List<object> { field, value, value };
        if (ctx.EntryId is { } id)
        {
            sql += " and d.id <> ?";
            args.Add(id);
        }

        var type = ctx.ContentType.ToLowerInvariant();
        var clash = await ctx.Session.Query<Content>()
            .Where(x => x.ContentType.ToLower() == type && x.MatchesSql(sql, args.ToArray()))
            .AnyAsync(ct);
        return new(!clash, null);
    }

    /// <summary>The SQL for <paramref name="operand"/> with each normalise step applied in order.</summary>
    private static string Normalised(string operand, List<string>? steps)
    {
        foreach (var step in steps ?? [])
        {
            operand = step.ToLowerInvariant() switch
            {
                "trim" => $"trim({operand})",
                "lower" => $"lower({operand})",
                "digits" => $"regexp_replace({operand}, '[^0-9]', '', 'g')",
                _ => operand,
            };
        }
        return operand;
    }

    /// <summary>related, count and sum over the entries matching the condition's filter.</summary>
    private async Task<Held> ScanAsync(RuleCondition c, CancellationToken ct)
    {
        if (c.Kind.Equals("related", StringComparison.OrdinalIgnoreCase))
        {
            if (c.Ref is not null) return new(await RefAsync(c, ct) is { } entry && InStates(c, entry.LifecycleState), null);
            var (rows, error) = await RowsAsync(c, ct);
            return error is not null ? new(false, error) : new(rows.Count > 0, null);
        }

        var (total, failed) = c.Kind.Equals("sum", StringComparison.OrdinalIgnoreCase) ? await SumAsync(c, ct) : await CountAsync(c, ct);
        return failed is not null
            ? new(false, failed)
            : new(RuleConditions.Compare(c.Op, total.ToString(CultureInfo.InvariantCulture), c.Value), null);
    }

    private async Task<(decimal Total, string? Error)> CountAsync(RuleCondition c, CancellationToken ct)
    {
        var (rows, error) = await RowsAsync(c, ct);
        return (rows.Count, error);
    }

    private async Task<(decimal Total, string? Error)> SumAsync(RuleCondition c, CancellationToken ct)
    {
        var (sources, error) = await SourcesAsync(c, ownWhenBare: false, ct);
        if (error is not null) return (0, error);

        var path = Fill(c.Sum) ?? "";
        var total = 0m;
        foreach (var value in sources.SelectMany(data => RulePath.Spread(RulePath.Values(data, path, Values))))
        {
            if (RuleConditions.TryNumber(RulePath.TextOf(value), out var n)) total += n;
        }
        return (total, null);
    }

    private async Task<(List<Content> Rows, string? Error)> RowsAsync(RuleCondition c, CancellationToken ct)
    {
        var type = (c.ContentType ?? ctx.ContentType).ToLowerInvariant();
        var sql = new List<string>();
        var args = new List<object>();
        foreach (var pair in (c.Where ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq].Trim();
            if (!ValidName(name)) return ([], InvalidName(name));
            // Ignoring case, like the eq op.
            sql.Add($"lower({FieldValue}) = lower(?)");
            args.Add(name);
            args.Add(Fill(eq < 0 ? "" : pair[(eq + 1)..].Trim())!);
        }
        if (c.State is not null)
        {
            // Ignoring case, like the eq op; State is a comma list.
            sql.Add("lower(d.data ->> 'LifecycleState') = ANY(?)");
            args.Add(States(c.State).Select(s => s.ToLowerInvariant()).ToArray());
        }

        var where = sql.Count == 0 ? "true" : string.Join(" and ", sql);
        var rows = await ctx.Session.Query<Content>()
            .Where(x => x.ContentType.ToLower() == type && x.MatchesSql(where, args.ToArray()))
            .Take(cap + 1).ToListAsync(ct);
        return rows.Count > cap ? ([], $"Rule scan is too large (over {cap} entries).") : (rows.ToList(), null);
    }

    /// <summary>Calls the named lookup and keeps its value for the rule's templates.</summary>
    private async Task<Held> LookupAsync(RuleCondition c, CancellationToken ct)
    {
        var (value, error) = await ResolveAsync(c, ct);
        if (error is not null) return new(false, error);

        var text = value.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) : value.Text;
        return new(c.Op.ToLowerInvariant() switch
        {
            "empty" => !value.Found,
            "notempty" => value.Found,
            _ => value.Found && RuleConditions.Compare(c.Op, text, c.Value),
        }, null);
    }

    private async Task<(RuleLookupValue Value, string? Error)> ResolveAsync(RuleCondition c, CancellationToken ct)
    {
        var impl = lookups.FirstOrDefault(l => string.Equals(l.Name, c.Lookup, StringComparison.OrdinalIgnoreCase));
        if (impl is null) return (new(false, null, null), $"No rule lookup named '{c.Lookup}' is registered.");

        var args = (c.Args ?? []).ToDictionary(p => p.Key, p => Fill(p.Value)!);
        var value = await impl.ResolveAsync(args, ctx.Session, ct);
        resolved[c.Lookup!] = value;
        return (value, null);
    }

    /// <summary>Works out one let and keeps it under its name. Null, or the error that fails the rule.</summary>
    public async Task<string?> LetAsync(RuleCondition let, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(let.As)) return "A let needs a name in 'as'.";
        object? value;
        switch (let.Kind.ToLowerInvariant())
        {
            case "sum" or "count":
                var (total, error) = let.Kind.Equals("sum", StringComparison.OrdinalIgnoreCase) ? await SumAsync(let, ct) : await CountAsync(let, ct);
                if (error is not null) return error;
                value = total;
                break;
            case "field":
                value = await FieldValueAsync(let, ct);
                break;
            case "lookup":
                var (found, failed) = await ResolveAsync(let, ct);
                if (failed is not null) return failed;
                value = found.Found ? (object?)found.Number ?? found.Text : null;
                break;
            case "value":
                value = Fill(let.Value);
                break;
            default:
                return $"Let '{let.As}' cannot use kind '{let.Kind}'.";
        }

        Values.Lets[let.As] = value;
        return null;
    }

    /// <summary>The stored entry's value at Field compared with Op and Value. On create the stored value reads empty.</summary>
    private Held Stored(RuleCondition c)
    {
        var saved = ctx.Existing?.Data.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
        return new(RuleConditions.Compare(c.Op, saved is null ? null : Read(saved, c.Field), Fill(c.Value)), null);
    }
}
