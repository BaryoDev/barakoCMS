using System.Reflection;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Weasel.Core;

namespace barakoCMS.Modules;

/// <summary>
/// What each module's schema registration would do to the database, worked out before anything
/// is applied.
/// </summary>
/// <remarks>
/// Production runs <c>AutoCreate.CreateOnly</c>: a missing table is created with its indexes, an
/// existing one is never altered. A module whose documents are new therefore boots on any database,
/// and a module that adds an index to a table that already exists fails at startup inside Marten,
/// several layers down and without the module's name. This asks Marten for the migration it would
/// apply, attributes every object in it to a module, and refuses by name when the store's policy
/// would refuse the change anyway.
///
/// Attribution is by the assembly the document type ships in: a module owns the types in its own
/// assembly and in <see cref="IBarakoModule.SchemaAssemblies"/>, and everything else is core. The
/// one way a module reaches an object it does not own is the deprecated
/// <see cref="IBarakoModule.ConfigureMarten"/>, which hands over the raw <see cref="StoreOptions"/>,
/// so a change to an object nobody owns is attributed to every enabled module that overrides it.
/// Marten stores schema alterations on deferred builders, so which of two such modules made the
/// change cannot be told apart here; both are named.
/// </remarks>
internal static class ModuleSchemaPreflight
{
    /// <summary>
    /// Whether the preflight runs. Unset means yes on a <c>CreateOnly</c> store and no otherwise,
    /// which keeps today's behaviour everywhere the check would only report what the store is
    /// about to apply anyway.
    /// </summary>
    public const string EnabledKey = "BarakoCMS:Modules:SchemaPreflight";

    /// <summary>The owner name used for objects no module claims.</summary>
    public const string CoreName = "core";

    public static bool IsEnabled(IConfiguration configuration, AutoCreate autoCreate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetValue<bool?>(EnabledKey) ?? autoCreate == AutoCreate.CreateOnly;
    }

    /// <summary>
    /// The migration Marten would apply, attributed per module and to core, for every database the
    /// store knows.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ModuleSchemaFinding>> ComputeAsync(
        IDocumentStore store, IReadOnlyList<IBarakoModule> modules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(modules);

        var owners = modules.Select(Owner.Of).ToList();
        var direct = owners.Where(o => o.ConfiguresStoreDirectly).ToList();

        var findings = new Dictionary<string, FindingBuilder>(StringComparer.Ordinal)
        {
            [CoreName] = new(CoreName),
        };
        foreach (var owner in owners)
            findings[owner.Name] = new FindingBuilder(owner.Name);

        var databases = await store.Storage.AllDatabases();
        foreach (var database in databases)
        {
            var migration = await database.CreateMigrationAsync(ct);
            if (migration.Deltas.Count == 0)
                continue;

            var storageTypes = StorageTypesByObjectName(database);
            var prefix = databases.Count > 1 ? $"{database.Identifier}: " : string.Empty;

            foreach (var delta in migration.Deltas)
            {
                if (delta.Difference == SchemaPatchDifference.None)
                    continue;

                var name = prefix + delta.SchemaObject.Identifier.QualifiedName;
                var storageType = storageTypes.GetValueOrDefault(delta.SchemaObject.Identifier.QualifiedName);
                var owner = storageType is null ? null : owners.FirstOrDefault(o => o.Owns(storageType));

                if (owner is not null)
                {
                    findings[owner.Name].Add(name, delta.Difference, reachedThroughConfigureMarten: false);
                }
                else if (delta.Difference != SchemaPatchDifference.Create && direct.Count > 0)
                {
                    foreach (var suspect in direct)
                        findings[suspect.Name].Add(name, delta.Difference, reachedThroughConfigureMarten: true);
                }
                else
                {
                    findings[CoreName].Add(name, delta.Difference, reachedThroughConfigureMarten: false);
                }
            }
        }

        return findings.ToDictionary(f => f.Key, f => f.Value.Build(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Refuses when a module wants something the store's policy will not apply. Core's own deltas
    /// are left to Marten, whose message docs/upgrading-to-4.0.md already documents.
    /// </summary>
    /// <exception cref="InvalidOperationException">At least one module wants a refused change.</exception>
    public static void AssertAllowed(IReadOnlyDictionary<string, ModuleSchemaFinding> findings, AutoCreate autoCreate)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var refused = findings.Values
            .Where(f => f.Owner != CoreName)
            .Select(f => (f.Owner, Objects: f.Changes.Where(c => !Allows(autoCreate, c.Difference)).ToList()))
            .Where(f => f.Objects.Count > 0)
            .OrderBy(f => f.Owner, StringComparer.Ordinal)
            .ToList();

        if (refused.Count == 0)
            return;

        var lines = refused.Select(f =>
            $"{f.Owner}: {string.Join(", ", f.Objects.Select(o => o.ReachedThroughConfigureMarten ? $"{o.Name} (owned by core, reached through ConfigureMarten)" : o.Name))}");

        throw new InvalidOperationException(
            $"Schema preflight refused to start. This store runs AutoCreate.{autoCreate}, which "
            + "creates a missing object and never alters one that exists, and these modules want a "
            + $"change to an existing database object: {string.Join("; ", lines)}. Apply the change "
            + "first (dotnet barakoCMS.dll db-patch, see docs/upgrading-to-4.0.md), or run the store "
            + "with AutoCreate.CreateOrUpdate, which this host uses when ASPNETCORE_ENVIRONMENT is "
            + $"Development. {EnabledKey}=false skips this check and leaves the refusal to Marten.");
    }

    /// <summary>Whether a store under <paramref name="autoCreate"/> applies a delta of this kind.</summary>
    public static bool Allows(AutoCreate autoCreate, SchemaPatchDifference difference) => autoCreate switch
    {
        AutoCreate.All => true,
        AutoCreate.CreateOrUpdate => difference != SchemaPatchDifference.Invalid,
        _ => difference is SchemaPatchDifference.None or SchemaPatchDifference.Create,
    };

    private static Dictionary<string, Type> StorageTypesByObjectName(Weasel.Core.Migrations.IDatabase database)
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var feature in database.BuildFeatureSchemas())
        {
            if (feature.StorageType is null)
                continue;

            foreach (var schemaObject in feature.Objects)
            {
                map[schemaObject.Identifier.QualifiedName] = feature.StorageType;
                foreach (var name in schemaObject.AllNames())
                    map.TryAdd(name.QualifiedName, feature.StorageType);
            }
        }

        return map;
    }

    private sealed record Owner(string Name, HashSet<Assembly> Assemblies, bool ConfiguresStoreDirectly)
    {
        public static Owner Of(IBarakoModule module)
        {
            var assemblies = new HashSet<Assembly> { module.GetType().Assembly };
            foreach (var assembly in module.SchemaAssemblies)
                assemblies.Add(assembly);

            return new Owner(
                module.Name,
                assemblies,
                barakoCMS.Extensions.ServiceCollectionExtensions.OverridesConfigureMarten(module));
        }

        public bool Owns(Type storageType) => Assemblies.Contains(storageType.Assembly);
    }

    private sealed class FindingBuilder(string owner)
    {
        private readonly List<string> _new = new();
        private readonly List<ModuleSchemaChange> _changes = new();

        public void Add(string name, SchemaPatchDifference difference, bool reachedThroughConfigureMarten)
        {
            if (difference == SchemaPatchDifference.Create)
                _new.Add(name);
            else
                _changes.Add(new ModuleSchemaChange(name, difference, reachedThroughConfigureMarten));
        }

        public ModuleSchemaFinding Build() => new(owner, _new.ToArray(), _changes.ToArray());
    }
}

/// <summary>
/// What one module (or core) wants from the database: objects that do not exist yet, and objects
/// that exist and would have to change.
/// </summary>
internal sealed record ModuleSchemaFinding(
    string Owner,
    IReadOnlyList<string> NewObjects,
    IReadOnlyList<ModuleSchemaChange> Changes)
{
    public string State => Changes.Count > 0 ? ModuleSchemaState.NeedsMigration : ModuleSchemaState.Ready;
}

/// <param name="Name">The qualified object name, prefixed by the database identifier when the store spans more than one.</param>
/// <param name="Difference">Marten's classification: an update, or a change it cannot express in place.</param>
/// <param name="ReachedThroughConfigureMarten">
/// True when the object belongs to core and this module is named because it overrides
/// <see cref="IBarakoModule.ConfigureMarten"/>, which is the only hook that can reach it.
/// </param>
internal sealed record ModuleSchemaChange(string Name, SchemaPatchDifference Difference, bool ReachedThroughConfigureMarten);

/// <summary>The values <c>GET /api/modules</c> reports in <c>schemaState</c>.</summary>
internal static class ModuleSchemaState
{
    public const string Ready = "ready";
    public const string NeedsMigration = "needs-migration";
    public const string Unknown = "unknown";
}

/// <summary>
/// The preflight's result for this process, filled once at boot and read by <c>GET /api/modules</c>.
/// Empty until the preflight has run, or forever when it is switched off, and the endpoint reports
/// that as <see cref="ModuleSchemaState.Unknown"/> rather than guessing.
/// </summary>
internal sealed class ModuleSchemaReport
{
    private volatile IReadOnlyDictionary<string, ModuleSchemaFinding>? _findings;

    public bool Computed => _findings is not null;

    public void Record(IReadOnlyDictionary<string, ModuleSchemaFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        _findings = findings;
    }

    public ModuleSchemaFinding? For(string owner) => _findings?.GetValueOrDefault(owner);

    public IReadOnlyCollection<ModuleSchemaFinding> All =>
        _findings?.Values.ToArray() ?? Array.Empty<ModuleSchemaFinding>();
}
