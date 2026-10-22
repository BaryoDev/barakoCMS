namespace barakoCMS.Infrastructure.Rules;

/// <summary>What a rule reads besides the entry: the clock, the zone, the lets worked out so far and, inside each, the outer entry.</summary>
internal sealed record RuleValues(DateTimeOffset Now, TimeZoneInfo Zone, Dictionary<string, object?> Lets, IDictionary<string, object?>? Parent = null)
{
    public static RuleValues Utc(TimeProvider clock) => new(clock.GetUtcNow(), TimeZoneInfo.Utc, new(StringComparer.OrdinalIgnoreCase));
}
