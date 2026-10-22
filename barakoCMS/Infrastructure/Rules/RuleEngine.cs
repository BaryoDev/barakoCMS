using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Runs a content type's rules in declared order. The first rule that fails answers.</summary>
internal sealed class RuleEngine(IEnumerable<IRuleLookup> lookups, TimeProvider? clock = null) : IRuleEngine
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<RuleResult> EvaluateAsync(RuleWhen when, RuleContext ctx, CancellationToken ct)
    {
        var lowered = ctx.ContentType.ToLowerInvariant();
        var type = await ctx.Session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (type?.Rules is not { Count: > 0 } rules) return RuleResult.Pass;

        var now = _clock.GetUtcNow();
        TimeZoneInfo? site = null;
        var from = ctx.FromState ?? ctx.Existing?.LifecycleState;
        foreach (var rule in rules.Where(r => Applies(r, when, ctx.Transition, from)))
        {
            var zone = rule.TimeZone is { Length: > 0 } id ? TemplateExpression.Zone(id)
                : RuleClock.Used(rule) ? site ??= await SiteZoneAsync(ctx.Session, ct)
                : TimeZoneInfo.Utc;
            if (zone is null) return new(false, $"Time zone '{rule.TimeZone}' is not known on this server.", rule.Name);

            var failed = await RunAsync(rule, ctx, new RuleValues(now, zone, new(StringComparer.OrdinalIgnoreCase)), ct);
            if (failed is not null) return failed;
        }

        return RuleResult.Pass;
    }

    /// <summary>One rule: null when it passed or was skipped, the refusal otherwise.</summary>
    private async Task<RuleResult?> RunAsync(RuleDefinition rule, RuleContext ctx, RuleValues values, CancellationToken ct)
    {
        var resolved = new Dictionary<string, RuleLookupValue>(StringComparer.OrdinalIgnoreCase);
        var scan = new RuleScan(ctx, lookups, resolved, values: values);
        try
        {
            return await ConditionsAsync(rule, ctx, scan, resolved, values, ct);
        }
        catch (RuleExpressionException ex)
        {
            return new(false, $"Rule '{rule.Name}': {ex.Message}", rule.Name);
        }
    }

    /// <summary>If, then the lets in order, then All, then Set.</summary>
    private static async Task<RuleResult?> ConditionsAsync(
        RuleDefinition rule, RuleContext ctx, RuleScan scan, Dictionary<string, RuleLookupValue> resolved, RuleValues values, CancellationToken ct)
    {
        foreach (var guard in rule.If ?? [])
        {
            var guarded = await scan.HoldsAsync(guard, ct);
            if (guarded.Error is not null) return Fail(rule, guarded.Error, scan);
            if (guarded.Passed == guard.Negate) return null;
        }

        foreach (var let in rule.Let ?? [])
        {
            var error = await scan.LetAsync(let, ct);
            if (error is not null) return Fail(rule, error, scan);
        }

        foreach (var condition in rule.All)
        {
            var held = await scan.HoldsAsync(condition, ct);
            if (held.Error is not null) return Fail(rule, held.Error, scan);
            if (held.Passed == condition.Negate) return Fail(rule, null, scan, condition.Message);
        }

        var set = ApplySet(rule, ctx, resolved, values);
        return set is null ? null : Fail(rule, set, scan);
    }

    private static bool Applies(RuleDefinition rule, RuleWhen when, string? transition, string? from) =>
        rule.When == when
        && (rule.Transition is null || string.Equals(rule.Transition, transition, StringComparison.OrdinalIgnoreCase))
        && (rule.InState is null || string.Equals(rule.InState, from, StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal: an error as given, else the condition's message, else the rule's, filled as templates.</summary>
    private static RuleResult Fail(RuleDefinition rule, string? detail, RuleScan scan, string? conditionMessage = null)
    {
        var message = conditionMessage is { Length: > 0 } ? conditionMessage : rule.Message;
        return new(false, detail ?? (message.Length > 0 ? scan.Fill(message) : $"Rule '{rule.Name}' failed."), rule.Name);
    }

    private static string? ApplySet(
        RuleDefinition rule, RuleContext ctx, Dictionary<string, RuleLookupValue> resolved, RuleValues values)
    {
        foreach (var (field, template) in rule.Set)
        {
            var filled = RuleTemplates.Fill(template, ctx.Data, ctx.UserId, resolved, out var missing, values);
            if (missing is not null) return $"Rule '{rule.Name}' could not fill '{field}': {missing} has no value.";
            ctx.Data[field] = filled;
        }
        return null;
    }

    /// <summary>The oldest published site entry's TimeZone, read the way templates read it. Unknown is UTC.</summary>
    private static async Task<TimeZoneInfo> SiteZoneAsync(IDocumentSession session, CancellationToken ct)
    {
        var site = await session.Query<Content>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt).Take(1).FirstOrDefaultAsync(ct);
        return TemplateExpression.Zone(EntryUrls.Setting(site, "TimeZone")) ?? TimeZoneInfo.Utc;
    }
}
