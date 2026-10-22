using System.Globalization;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Evaluates one rule's conditions against a context, sharing the lookups it resolved.</summary>
internal sealed partial class RuleScan(
    RuleContext ctx, IEnumerable<IRuleLookup> lookups, Dictionary<string, RuleLookupValue> resolved,
    int cap = 5000, TimeSpan? regexTimeout = null, RuleValues? values = null)
{
    /// <summary>Whether a condition held, or the error that stops the rule.</summary>
    internal readonly record struct Held(bool Passed, string? Error);

    /// <summary>The clock, zone, lets and outer entry this scan reads.</summary>
    internal RuleValues Values { get; } = values ?? RuleValues.Utc(TimeProvider.System);

    /// <summary>A template filled from this entry, the lookups, the lets and the clock.</summary>
    public string? Fill(string? template) =>
        template is null ? null : RuleTemplates.Fill(template, ctx.Data, ctx.UserId, resolved, out _, Values);

    private string? Read(IDictionary<string, object?> data, string? path) => RulePath.Text(data, Fill(path), Values);

    /// <summary>Whether the condition holds. A condition whose guards do not all hold counts as met.</summary>
    public async Task<Held> HoldsAsync(RuleCondition c, CancellationToken ct)
    {
        foreach (var guard in c.If ?? [])
        {
            var guarded = await KindAsync(guard, ct);
            if (guarded.Error is not null) return guarded;
            if (guarded.Passed == guard.Negate) return new(!c.Negate, null);
        }

        return await KindAsync(c, ct);
    }

    private async Task<Held> KindAsync(RuleCondition c, CancellationToken ct)
    {
        switch (c.Kind.ToLowerInvariant())
        {
            case "field":
                return new(RuleConditions.Compare(c.Op, await FieldValueAsync(c, ct), Fill(c.Value)), null);
            case "length": return new(RuleFieldKinds.Length(c, ctx.Data), null);
            case "pattern": return RuleFieldKinds.Pattern(c, ctx.Data, regexTimeout ?? RuleFieldKinds.DefaultTimeout);
            case "itemcount":
                var counted = RuleFieldKinds.Items(ctx.Data, c.Field);
                return counted.Error is not null
                    ? new(false, counted.Error)
                    : new(RuleConditions.Compare(c.Op, counted.Items.Count.ToString(CultureInfo.InvariantCulture), c.Value), null);
            case "state":
                return new(RuleConditions.Compare(c.Op, await StateAsync(c, ct), c.Value), null);
            case "each": return await EachAsync(c, ct);
            case "changed":
                var stored = ctx.Existing?.Data.ToDictionary(p => p.Key, p => (object?)p.Value);
                return new(RuleFieldKinds.Changed(c, ctx.Data, stored), null);
            default:
                return await OtherKindAsync(c, ct);
        }
    }

    private async Task<Held> EachAsync(RuleCondition c, CancellationToken ct)
    {
        var (items, error) = RuleFieldKinds.Items(ctx.Data, c.Field);
        if (error is not null) return new(false, error);

        foreach (var item in items)
        {
            var child = new RuleScan(ctx with { Data = item }, lookups, resolved, cap, regexTimeout, Values with { Parent = ctx.Data });
            foreach (var inner in c.Each ?? [])
            {
                var held = await child.HoldsAsync(inner, ct);
                if (held.Error is not null) return held;
                if (held.Passed == inner.Negate)
                    return inner.Message is { Length: > 0 } m && !c.Negate ? new(false, child.Fill(m)) : new(false, null);
            }
        }
        return new(true, null);
    }

    private async Task<Held> OtherKindAsync(RuleCondition c, CancellationToken ct)
    {
        switch (c.Kind.ToLowerInvariant())
        {
            case "unique": return await UniqueAsync(c, ct);
            case "related" or "count" or "sum": return await ScanAsync(c, ct);
            case "lookup": return await LookupAsync(c, ct);
            case "stored": return Stored(c);
            case "member": return await MemberAsync(c, ct);
            case "covers": return await CoversAsync(c, ct);
            case "distinct": return Distinct(c);
            case "notrequester": return new(false, "Condition kind 'notRequester' is provided by the Approvals module.");
            default: return new(false, $"A rule uses an unknown condition kind '{c.Kind}'.");
        }
    }
}
