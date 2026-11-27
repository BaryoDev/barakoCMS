namespace barakoCMS.Features.Queries.Run;

/// <summary>
/// The SQL that reads one field of an entry's data, shared by the aggregate query and the rollup
/// aggregate so a value is skipped or parsed the same way in both.
/// </summary>
/// <remarks>
/// Each method takes the key as SQL text that gives the lowered key to match. A caller passes a
/// name its own pattern checked, or a bound parameter. The field name is never written into the
/// text here.
/// </remarks>
internal static class EntryFieldSql
{
    private const string Day = @"[1-9][0-9]{3}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])";
    // {0,1} and not ?, because Marten reads every question mark in a raw query as a parameter.
    private const string Clock = @"[T ]([01][0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9]([.][0-9]{1,9}){0,1}){0,1}";
    private const string Offset = @"(Z|[+-](0[0-9]|1[0-5]):{0,1}[0-5][0-9])";

    public static string Find(string key, string alias, string expression) =>
        $"(SELECT {expression} FROM jsonb_each({alias}.data -> 'Data') e WHERE lower(e.key) = {key} LIMIT 1)";

    public static string Text(string key, string alias) => Find(key, alias, "e.value #>> '{}'");

    public static string Number(string key, string alias) =>
        Find(key, alias, "CASE WHEN jsonb_typeof(e.value) = 'number' THEN (e.value #>> '{}')::numeric END");

    /// <summary>
    /// A stored date as an instant. A value with no zone on it is read in <paramref name="zone"/>, not
    /// the session's. A value that is not a real calendar date is null, so it is left out of buckets
    /// and ranges instead of failing the whole run.
    /// </summary>
    public static string DateValue(string key, string alias, string zone)
    {
        const string s = "(e.value #>> '{}')";
        const string inMonth =
            "substr(" + s + ", 9, 2)::int <= extract(day from ((substr(" + s + ", 1, 7) || '-01')::date + interval '1 month' - interval '1 day'))";

        return Find(key, alias,
            "CASE WHEN jsonb_typeof(e.value) = 'string' THEN CASE "
          + $"WHEN {s} ~ '^{Day}$' THEN CASE WHEN {inMonth} THEN ({s}::date)::timestamp AT TIME ZONE {zone} END "
          + $"WHEN {s} ~ '^{Day}{Clock}$' THEN CASE WHEN {inMonth} THEN {s}::timestamp AT TIME ZONE {zone} END "
          + $"WHEN {s} ~ '^{Day}{Clock}{Offset}$' THEN CASE WHEN {inMonth} THEN {s}::timestamptz END "
          + "END END");
    }

    /// <summary>The ids a reference field holds, lower case, whether it holds one or a list.</summary>
    public static string References(string key, string alias) =>
        Find(key, alias,
            "CASE jsonb_typeof(e.value) "
          + "WHEN 'array' THEN ARRAY(SELECT lower(x) FROM jsonb_array_elements_text(e.value) x) "
          + "WHEN 'string' THEN ARRAY[lower(e.value #>> '{}')] END");
}
