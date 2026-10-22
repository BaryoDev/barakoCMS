using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace barakoCMS.Infrastructure.Reservations;

/// <summary>
/// The three tables behind <see cref="barakoCMS.Core.Interfaces.IStockReservations"/>, created
/// through Marten's extended schema objects like the number counters.
/// </summary>
internal static class StockTables
{
    public const string Counters = "barako_stock_counters";
    public const string Holds = "barako_stock_holds";
    public const string Movements = "barako_stock_movements";

    public static IEnumerable<Table> Build(string schema)
    {
        var counters = new Table(new PostgresqlObjectName(schema, Counters));
        counters.AddColumn<string>("tenant_id").AsPrimaryKey();
        counters.AddColumn<string>("counter").AsPrimaryKey();
        counters.AddColumn<long>("balance").NotNull();
        counters.AddColumn<long>("floor").NotNull();
        yield return counters;

        var holds = new Table(new PostgresqlObjectName(schema, Holds));
        holds.AddColumn<string>("tenant_id").AsPrimaryKey();
        holds.AddColumn<Guid>("id").AsPrimaryKey();
        holds.AddColumn<string>("counter").NotNull();
        holds.AddColumn<long>("quantity").NotNull();
        holds.AddColumn<string>("status").NotNull();
        holds.AddColumn<string>("reference").NotNull();
        holds.AddColumn<DateTimeOffset>("created_at").NotNull();
        holds.AddColumn<DateTimeOffset>("expires_at");
        holds.AddColumn<DateTimeOffset>("settled_at");
        holds.AddIndex(i =>
        {
            i.IndexName = "idx_barako_stock_holds_open";
            i.Columns = ["tenant_id", "counter"];
            i.Predicate = "status = 'held'";
        });
        yield return holds;

        var movements = new Table(new PostgresqlObjectName(schema, Movements));
        movements.AddColumn<string>("tenant_id").AsPrimaryKey();
        movements.AddColumn<Guid>("id").AsPrimaryKey();
        movements.AddColumn<string>("counter").NotNull();
        movements.AddColumn<string>("kind").NotNull();
        movements.AddColumn<long>("delta").NotNull();
        movements.AddColumn<Guid>("hold_id");
        movements.AddColumn<string>("reference").NotNull();
        movements.AddColumn<DateTimeOffset>("occurred_at").NotNull();
        movements.AddIndex(i =>
        {
            i.IndexName = "idx_barako_stock_movements_counter";
            i.Columns = ["tenant_id", "counter", "occurred_at"];
        });
        yield return movements;
    }
}
