using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.ContentType.ChangeField;

/// <summary>How many entries hold a value a convert or a remove would drop.</summary>
internal static class ChangeFieldValues
{
    private const string HoldsValueSql =
        "exists (select 1 from jsonb_each(d.data -> 'Data') e where lower(e.key) = ? and e.value <> 'null'::jsonb)";

    /// <summary>
    /// Counted before anything is changed, so the refusal can name it and the change can record it.
    /// One query either way: a remove counts the entries holding a value, and a convert reads each
    /// distinct stored value once, with how many entries hold it, rather than every entry.
    /// </summary>
    public static async Task<int> LostAsync(IDocumentSession session, ContentTypeDefinition definition, FieldChange change, CancellationToken ct)
    {
        var lowered = definition.Name.ToLower();
        var field = change.Field.ToLower();
        if (change.Kind == FieldChangeKind.Remove)
        {
            return await session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType.ToLower() == lowered && c.MatchesSql(HoldsValueSql, field))
                .CountAsync(ct);
        }

        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var held = await session.QueryAsync<HeldValue>(
            "select jsonb_build_object('Value', t.v, 'Entries', t.n) from ("
            + "select e.value as v, count(*) as n "
            + $"from {schema}.mt_doc_contents d cross join lateral jsonb_each(d.data -> 'Data') e "
            + "where d.tenant_id = ? and lower(d.data ->> 'ContentType') = ? and lower(e.key) = ? and e.value <> 'null'::jsonb "
            + "group by e.value) t",
            ct, session.TenantId, lowered, field);

        return held.Where(h => h.Value is not null && FieldConversion.Convert(h.Value, change) is null).Sum(h => h.Entries);
    }

    /// <summary>One distinct stored value of the field, read as an entry's data reads it, and how many entries hold it.</summary>
    internal sealed class HeldValue
    {
        public object? Value { get; set; }

        public int Entries { get; set; }
    }
}
