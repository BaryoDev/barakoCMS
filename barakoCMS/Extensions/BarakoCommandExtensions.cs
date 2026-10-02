using barakoCMS.Infrastructure.Migrations;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace barakoCMS.Extensions;

public static class BarakoCommandExtensions
{
    /// <summary>
    /// The last line of a host's <c>Program.cs</c>: runs <c>db-migrate</c> when that is the first
    /// argument, and otherwise hands over to JasperFx, which runs <c>db-assert</c>, <c>db-patch</c>
    /// and <c>db-apply</c>, or serves when no command was named.
    /// </summary>
    /// <returns>The process exit code. A failed command is a return value, not an exception.</returns>
    public static async Task<int> RunBarakoCommandsAsync(this IHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        if (!MigrationCommand.IsNamed(args))
        {
            // db-apply creates the schema on an empty database just as a start does, so it owes that
            // database the same ledger rows, and for the same reason: written first.
            if (args.Length > 0 && args[0] == "db-apply")
            {
                await host.Services.ReconcileMigrationLedgerAsync(
                    host.Services.GetRequiredService<IDocumentStore>());
            }

            return await JasperFx.CommandLineHostingExtensions.RunJasperFxCommands(host, args);
        }

        var store = host.Services.GetRequiredService<IDocumentStore>();
        var shipped = ShippedMigrations.Discover(EnabledModules(host.Services));
        return await MigrationCommand.RunAsync(LedgerFor(store), shipped, args[1..], Console.Out);
    }

    /// <summary>
    /// Brings the ledger and a starting host into agreement, and says which migrations have not run.
    /// </summary>
    /// <remarks>
    /// On a database nothing has started against, every shipped migration is recorded as baselined
    /// before the schema is created. On any other database this only reads, and logs one warning
    /// when the ledger does not match the build. It never runs a migration and never stops a start:
    /// a ledger that cannot be read is a warning, because a host that booted yesterday has to boot
    /// today.
    /// </remarks>
    /// <returns>Pending migration ids by owner, for the schema preflight's refusal.</returns>
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReconcileMigrationLedgerAsync(
        this IServiceProvider services, IDocumentStore store, CancellationToken ct = default)
    {
        var none = new Dictionary<string, IReadOnlyList<string>>();
        try
        {
            var shipped = ShippedMigrations.Discover(EnabledModules(services));
            if (shipped.Count == 0)
                return none;

            var ledger = LedgerFor(store);
            var baselined = await ledger.BaselineIfFreshAsync(shipped, ct);
            if (baselined is null)
            {
                Log.Warning("A db-migrate run holds the migration lock, so this start did not read the migration ledger.");
                return none;
            }

            if (baselined.Count > 0)
            {
                Log.Information(
                    "New database: recorded {Count} shipped migration(s) as baselined. This start creates the schema they would have changed.",
                    baselined.Count);
                return none;
            }

            var status = await ledger.StatusAsync(shipped, ct);
            if (status.Current)
                return none;

            var pending = status.In(MigrationState.Pending);
            Log.Warning(
                "The migration ledger does not match this build. Pending: [{Pending}]. Not needed and not yet recorded: [{NotNeeded}]. "
                + "Changed since they were recorded: [{Changed}]. Run this image with db-migrate in place of a normal start. See docs/migrations.md.",
                string.Join(", ", pending.Select(l => l.Migration.Key)),
                string.Join(", ", status.In(MigrationState.NotNeeded).Select(l => l.Migration.Key)),
                string.Join(", ", status.In(MigrationState.Changed).Select(l => l.Migration.Key)));

            return pending
                .GroupBy(l => l.Migration.Owner, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(l => l.Migration.Id).ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "The migration ledger could not be read, so this start does not know which migrations are pending. Run db-migrate --status to see why.");
            return none;
        }
    }

    private static List<IBarakoModule> EnabledModules(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetServices<IBarakoModule>().ToList();
    }

    // The store's own connection object, not one rebuilt from its ConnectionString: Npgsql takes the
    // password out of that string unless Persist Security Info is set.
    private static MigrationLedger LedgerFor(IDocumentStore store) =>
        new(() => store.Storage.Database.CreateConnection(),
            $"barakoCMS {typeof(MigrationLedger).Assembly.GetName().Version}");
}
