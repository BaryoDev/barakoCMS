using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace barakoCMS.Infrastructure.Numbering;

/// <summary>
/// One row per tenant, series and period, holding the last number handed out. Created through
/// Marten's extended schema objects, so the startup schema apply builds it like any other table
/// and CreateOnly in production creates it without ever altering it.
/// </summary>
internal static class NumberCounterTable
{
    public const string Name = "barako_number_counters";

    public static Table Build(string schema)
    {
        var table = new Table(new PostgresqlObjectName(schema, Name));
        table.AddColumn<string>("tenant_id").AsPrimaryKey();
        table.AddColumn<string>("series").AsPrimaryKey();
        table.AddColumn<string>("period").AsPrimaryKey();
        table.AddColumn<long>("last_value").NotNull();
        return table;
    }
}
