using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using barakoCMS.Modules;

namespace barakoCMS.Infrastructure.Migrations;

/// <summary>One SQL migration an assembly ships as an embedded resource.</summary>
/// <param name="Owner"><c>core</c>, or the name of the module whose assembly ships the file.</param>
/// <param name="Version">The folder the file sits in, a three part version.</param>
/// <param name="Name">The file name without <c>.sql</c>.</param>
/// <param name="Sql">The file, with line endings normalised to LF.</param>
/// <param name="Checksum">SHA-256 of <paramref name="Sql"/>, lower case hex.</param>
/// <param name="SkipWhen">
/// A query returning one boolean. True means the database does not need this file, and it is
/// recorded without being run.
/// </param>
/// <param name="Transactional">False when the file says it cannot run inside a transaction.</param>
internal sealed record ShippedMigration(
    string Owner,
    string Version,
    string Name,
    string Sql,
    string Checksum,
    string? SkipWhen,
    bool Transactional)
{
    public string Id => $"{Version}/{Name}";

    /// <summary>What the operator reads and types: <c>owner/version/name</c>.</summary>
    public string Key => $"{Owner}/{Id}";
}

/// <summary>Finds the migrations core and the enabled modules ship, and puts them in run order.</summary>
/// <remarks>
/// A migration is an embedded resource named <c>barako-migrations/&lt;version&gt;/&lt;name&gt;.sql</c>.
/// The owner is decided by the assembly the resource is in, never by anything in the file, so a
/// module cannot ship a file under core's name.
/// </remarks>
internal static class ShippedMigrations
{
    public const string CoreOwner = ModuleSchemaPreflight.CoreName;
    public const string ResourcePrefix = "barako-migrations/";

    private const string DirectivePrefix = "-- barako:";
    private const string SkipWhenDirective = "skip-when:";
    private const string NoTransactionDirective = "no-transaction";

    private static readonly Regex PathPattern = new(
        @"^(?<version>[0-9]+\.[0-9]+\.[0-9]+)/(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\.sql$",
        RegexOptions.CultureInvariant);

    /// <summary>Core's migrations, then each enabled module's, in the order they run.</summary>
    /// <exception cref="InvalidOperationException">
    /// A resource is misnamed, a directive is unknown, or two enabled modules ship the same assembly's
    /// migrations.
    /// </exception>
    public static IReadOnlyList<ShippedMigration> Discover(IEnumerable<IBarakoModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var core = typeof(ShippedMigrations).Assembly;
        var found = new List<ShippedMigration>(FromAssembly(CoreOwner, core));
        var claimedBy = new Dictionary<Assembly, string>();

        foreach (var module in modules.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var assemblies = new[] { module.GetType().Assembly }
                .Concat(module.SchemaAssemblies)
                .Where(a => a != core)
                .Distinct();

            foreach (var assembly in assemblies)
            {
                var shipped = FromAssembly(module.Name, assembly);
                if (shipped.Count == 0)
                    continue;

                if (module.Name == CoreOwner)
                {
                    throw new InvalidOperationException(
                        $"A module named '{CoreOwner}' cannot ship migrations: that name is the ledger's owner for core's own.");
                }

                // Two owners for one file would run it twice, and which module's name the ledger
                // carried would depend on which of them happened to be enabled.
                if (claimedBy.TryGetValue(assembly, out var other) && other != module.Name)
                {
                    throw new InvalidOperationException(
                        $"Modules '{other}' and '{module.Name}' both ship the migrations in "
                        + $"{assembly.GetName().Name}. One assembly's migrations need one owner.");
                }

                if (claimedBy.TryAdd(assembly, module.Name))
                    found.AddRange(shipped);
            }
        }

        var duplicate = found.GroupBy(m => m.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Migration {duplicate.Key} is shipped more than once.");

        return InRunOrder(found);
    }

    public static IReadOnlyList<ShippedMigration> FromAssembly(string owner, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var migrations = new List<ShippedMigration>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration '{resource}' could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(Parse(owner, resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return migrations;
    }

    /// <param name="owner"><c>core</c> or a module name.</param>
    /// <param name="path"><c>&lt;version&gt;/&lt;name&gt;.sql</c>, the resource name after the prefix.</param>
    /// <param name="sql">The file's text.</param>
    public static ShippedMigration Parse(string owner, string path, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sql);

        if (owner.Contains('/'))
            throw new InvalidOperationException($"'{owner}' cannot own migrations: an owner name has no slash in it.");

        // MSBuild writes %(RecursiveDir) with the build machine's separator.
        var normalised = path.Replace('\\', '/');
        var match = PathPattern.Match(normalised);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Migration resource '{path}' shipped by {owner} is not named <major.minor.patch>/<name>.sql.");
        }

        var text = sql.Replace("\r\n", "\n").Replace('\r', '\n');
        string? skipWhen = null;
        var transactional = true;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(DirectivePrefix, StringComparison.Ordinal))
                continue;

            var directive = line[DirectivePrefix.Length..].Trim();
            if (directive == NoTransactionDirective)
            {
                transactional = false;
            }
            else if (directive.StartsWith(SkipWhenDirective, StringComparison.Ordinal))
            {
                if (skipWhen is not null)
                    throw new InvalidOperationException($"Migration {owner}/{normalised} has more than one skip-when line.");

                skipWhen = directive[SkipWhenDirective.Length..].Trim();
                if (skipWhen.Length == 0)
                    throw new InvalidOperationException($"Migration {owner}/{normalised} has an empty skip-when line.");
            }
            else
            {
                // A misspelt directive read as a comment would turn a guarded file into an unguarded one.
                throw new InvalidOperationException(
                    $"Migration {owner}/{normalised} has a directive this build does not know: '{line}'.");
            }
        }

        return new ShippedMigration(
            owner,
            match.Groups["version"].Value,
            match.Groups["name"].Value,
            text,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            skipWhen,
            transactional);
    }

    /// <summary>
    /// Core first, because a module may depend on core's objects and never the reverse. Then by
    /// owner, version and name.
    /// </summary>
    public static IReadOnlyList<ShippedMigration> InRunOrder(IEnumerable<ShippedMigration> migrations) =>
        migrations
            .OrderBy(m => m.Owner == CoreOwner ? 0 : 1)
            .ThenBy(m => m.Owner, StringComparer.Ordinal)
            .ThenBy(m => System.Version.Parse(m.Version))
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
}
