using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Reading one entry by id, and the kinds that check values against other entries or lists.</summary>
internal sealed partial class RuleScan
{
    /// <summary>
    /// The entry the condition's Ref names, or null when the id is not a plain Guid, the entry is
    /// missing, of another type, or belongs to a branch other than the request's.
    /// </summary>
    /// <remarks>
    /// Only the 36-character form is read, the one Where compares as text, so a rule cannot pass on
    /// "{id}" while a filter on the same field matches nothing. A branch-owned entry is read only
    /// from that branch: the id comes from the caller, and the rule's message can echo the entry.
    /// </remarks>
    private async Task<Content?> RefAsync(RuleCondition c, CancellationToken ct)
    {
        // TryParseExact still trims spaces, so the length check keeps " id " out too.
        if (Fill(c.Ref) is not { Length: 36 } text || !Guid.TryParseExact(text, "D", out var id)) return null;
        var entry = await ctx.Session.LoadAsync<Content>(id, ct);
        if (entry is null || (c.ContentType is not null && !string.Equals(entry.ContentType, c.ContentType, StringComparison.OrdinalIgnoreCase)))
            return null;
        return entry.BranchId is null || entry.BranchId == EventEnvelopeContext.CurrentBranch ? entry : null;
    }

    private static IDictionary<string, object?> Plain(Content entry) =>
        entry.Data.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>A comma list split into its trimmed, non-empty parts.</summary>
    private static string[] States(string? state) =>
        (state ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool InStates(RuleCondition c, string? state) =>
        c.State is null || States(c.State).Contains(state ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>The value at Field of this entry, or of the Ref entry when one is named (empty when it is missing).</summary>
    private async Task<string?> FieldValueAsync(RuleCondition c, CancellationToken ct)
    {
        if (c.Ref is null) return Read(ctx.Data, c.Field);
        return await RefAsync(c, ct) is { } entry ? Read(Plain(entry), c.Field) : null;
    }

    /// <summary>The state a state condition reads: the Ref entry's, else the one the entry is leaving.</summary>
    private async Task<string> StateAsync(RuleCondition c, CancellationToken ct) =>
        c.Ref is not null
            ? (await RefAsync(c, ct))?.LifecycleState ?? ""
            : ctx.FromState ?? ctx.Existing?.LifecycleState ?? "";

    /// <summary>The entries a condition reads: the Ref entry, else the matching rows, else (when ownWhenBare) this entry.</summary>
    private async Task<(List<IDictionary<string, object?>> Data, string? Error)> SourcesAsync(RuleCondition c, bool ownWhenBare, CancellationToken ct)
    {
        if (c.Ref is not null)
            return (await RefAsync(c, ct) is { } entry && InStates(c, entry.LifecycleState) ? [Plain(entry)] : [], null);
        if (ownWhenBare && c.ContentType is null && c.Where is null && c.State is null) return ([ctx.Data], null);
        var (rows, error) = await RowsAsync(c, ct);
        return (rows.Select(Plain).ToList(), error);
    }

    /// <summary>The values Of gives from each source, plus Also.</summary>
    private async Task<(HashSet<string> Values, string? Error)> OfValuesAsync(RuleCondition c, CancellationToken ct)
    {
        var found = new HashSet<string>(States(c.Also), StringComparer.OrdinalIgnoreCase);
        var (sources, error) = await SourcesAsync(c, ownWhenBare: true, ct);
        if (error is not null) return (found, error);
        var of = c.Of ?? "";
        foreach (var data in sources)
        {
            if (of.Contains("{{", StringComparison.Ordinal)) found.Add(RuleTemplates.Fill(of, data, ctx.UserId, resolved, out _, Values));
            else found.UnionWith(RulePath.Spread(RulePath.Values(data, of, Values)).Select(RulePath.TextOf).OfType<string>());
        }

        found.Remove("");
        return (found, null);
    }

    /// <summary>Every non-empty value at a path of this entry, a final list spread, after the normalise steps.</summary>
    private List<string> Texts(string? path, List<string>? normalise = null) =>
        RulePath.Spread(RulePath.Values(ctx.Data, Fill(path) ?? "", Values))
            .Select(RulePath.TextOf).OfType<string>().Where(v => v.Length > 0)
            .Select(v => NormaliseText(v, normalise)).ToList();

    /// <summary>The in-memory form of the normalise steps unique applies in SQL.</summary>
    private static string NormaliseText(string value, List<string>? steps) =>
        (steps ?? []).Aggregate(value, (v, step) => step.ToLowerInvariant() switch
        {
            "trim" => v.Trim(),
            "lower" => v.ToLowerInvariant(),
            "digits" => Regex.Replace(v, "[^0-9]", ""),
            _ => v,
        });

    private async Task<Held> MemberAsync(RuleCondition c, CancellationToken ct)
    {
        List<string> wanted = c.Field is not null ? Texts(c.Field) : [Fill(c.Value) ?? ""];
        wanted.RemoveAll(string.IsNullOrEmpty);
        if (wanted.Count == 0) return new(true, null);
        var (allowed, error) = await OfValuesAsync(c, ct);
        return error is not null ? new(false, error) : new(wanted.All(allowed.Contains), null);
    }

    private async Task<Held> CoversAsync(RuleCondition c, CancellationToken ct)
    {
        var (required, error) = await OfValuesAsync(c, ct);
        if (error is not null) return new(false, error);
        var have = new HashSet<string>(Texts(c.Field), StringComparer.OrdinalIgnoreCase);
        return new(required.All(have.Contains), null);
    }

    private Held Distinct(RuleCondition c)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new(Texts(c.Field, c.Normalise).All(seen.Add), null);
    }
}
