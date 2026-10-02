using System.Reflection;
using barakoCMS.Infrastructure.Migrations;
using barakoCMS.Modules;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Which migrations core and the modules ship, who owns each, and what a file may say about itself
/// (issue #901).
/// </summary>
public class ShippedMigrationTests
{
    private static readonly Assembly Core = typeof(ShippedMigrations).Assembly;

    private static readonly string[] ModuleOwned =
    [
        "4.2.0/stored-files-parent-index",
        "4.2.0/forms-public-forms",
        "4.5.0/email-sent-emails",
    ];

    /// <summary>
    /// The checksum of every migration file in a released version, as the ledgers out there hold it.
    /// </summary>
    /// <remarks>
    /// Add a line when a version is released. Never change one: a database that ran the file refuses
    /// to migrate while its recorded checksum differs from the shipped file's, so an edit here is an
    /// edit to every deployment's next upgrade. A further change is a new file.
    /// </remarks>
    private static readonly Dictionary<string, string> Released = new(StringComparer.Ordinal)
    {
        ["core/4.0.0/3.x-to-4.0"] = "d6beb5e32bb999d3cc080bcaba824bb1ce9b46ecc87d06232dd49564a69d232d",
        ["Forms/4.2.0/forms-public-forms"] = "e009a277501a3651668df63a6e0ccb23b4e904f04e115ab0bd53324fbc7e92de",
        ["core/4.2.0/site-share-links"] = "9974c4ec184040c620a38d7a17836ee7dade898d3be612a9cabce36a58d18d54",
        ["Files/4.2.0/stored-files-parent-index"] = "b57a23197cbd344020625fae8df4c268d2c449639209e1d73b71ef37d2ff96be",
        ["core/4.2.0/user-normalized-identity"] = "9aa6abcce493754bff4c36277f391adb95b32c8892a848f5db47e53d9aaa923c",
        ["core/4.3.0/collection-syncs"] = "5e6bfbb5045bb318be0febd8722406d5b6aaf2f81319fbd4eabcdf19e0cdab95",
        ["core/4.3.0/marten-9-37-event-store-columns"] = "5d6df1963b0b8a32852f05dc53c9c3efdf6b992bb378fbb35403be51b66a9f24",
        ["core/4.4.0/marten-9-38-quick-append-events"] = "4ad71cedf652c705f6603a0f7de7fb6a7b4d8660b6e55a50d3b5c8156f20dae9",
        ["Email.Resend/4.5.0/email-sent-emails"] = "c0e606da0fefdde0bc1b9bd644e8e63fb55deba306b547399063946762cdf996",
        ["core/4.5.0/refresh-token-hash-index"] = "3ee5a9d53d0cdc0a85907dcb10a546a467deb3718f48376d07f5aa7040b40c3e",
    };

    private static IReadOnlyList<ShippedMigration> FirstParty() =>
        ShippedMigrations.Discover(
            [new BarakoCMS.Forms.FormsModule(), new BarakoCMS.Files.FilesModule(), new BarakoCMS.Email.Resend.ResendEmailModule()]);

    private sealed class SecondOwnerOfTheFilesAssembly : IBarakoModule
    {
        public string Name => "Second Owner";

        public IEnumerable<Assembly> SchemaAssemblies =>
            [GetType().Assembly, typeof(BarakoCMS.Files.FilesModule).Assembly];
    }

    /// <summary>
    /// The embed is a glob, so the files on disk are the expectation: a file another change adds
    /// under <c>migrations/&lt;version&gt;/</c> is on both sides of this without an edit here.
    /// </summary>
    [Fact]
    public void Core_ships_every_forward_file_under_migrations_except_the_ones_a_module_ships()
    {
        var root = Path.Combine(RepositoryRoot(), "migrations");
        var onDisk = Directory.GetFiles(root, "*.sql", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("tenancy/", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).StartsWith("rollback-", StringComparison.Ordinal))
            .Select(path => path[..^".sql".Length])
            .Except(ModuleOwned)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var shipped = ShippedMigrations.FromAssembly(ShippedMigrations.CoreOwner, Core)
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        onDisk.Count.Should().BeGreaterThanOrEqualTo(7, "seven core files were released before the ledger");
        shipped.Should().Equal(onDisk);
        shipped.Should().Contain("4.0.0/3.x-to-4.0");
        shipped.Should().NotContain(id => id.Contains("rollback"), "db-migrate never runs a rollback");
    }

    [Fact]
    public void A_module_migration_ships_in_the_module_and_is_owned_by_it()
    {
        IBarakoModule[] modules =
        [
            new BarakoCMS.Forms.FormsModule(),
            new BarakoCMS.Files.FilesModule(),
            new BarakoCMS.Email.Resend.ResendEmailModule(),
        ];

        var all = ShippedMigrations.Discover(modules);
        var coreOnly = ShippedMigrations.Discover([]);

        var owned = all.Where(m => m.Owner != ShippedMigrations.CoreOwner).Select(m => m.Key).ToList();
        owned.Should().Equal(
            "Email.Resend/4.5.0/email-sent-emails",
            "Files/4.2.0/stored-files-parent-index",
            "Forms/4.2.0/forms-public-forms");
        coreOnly.Count.Should().BeGreaterThanOrEqualTo(7, "seven core files were released before the ledger");
        coreOnly.Should().HaveCount(all.Count - 3, "a module that is not enabled contributes nothing");
        coreOnly.Should().OnlyContain(m => m.Owner == ShippedMigrations.CoreOwner);
        coreOnly.Select(m => m.Id).Should().NotIntersectWith(ModuleOwned, "core must not also ship a module's file");
    }

    [Fact]
    public void Core_runs_first_and_each_owner_runs_in_version_then_name_order()
    {
        ShippedMigration At(string owner, string path) => ShippedMigrations.Parse(owner, path, "select 1;");

        var ordered = ShippedMigrations.InRunOrder(
        [
            At("Alpha", "1.0.0/b.sql"),
            At("core", "4.10.0/a.sql"),
            At("core", "4.9.0/z.sql"),
            At("Alpha", "1.0.0/a.sql"),
            At("core", "4.9.0/b.sql"),
        ]);

        ordered.Select(m => m.Key).Should().Equal(
            "core/4.9.0/b",
            "core/4.9.0/z",
            "core/4.10.0/a",
            "Alpha/1.0.0/a",
            "Alpha/1.0.0/b");
    }

    /// <summary>
    /// The rule that keeps an existing deployment safe on its first run: a file released before the
    /// ledger may already have been applied by hand, so it has to be able to tell.
    /// </summary>
    [Fact]
    public void Every_file_released_before_the_ledger_says_when_to_skip_it()
    {
        var preLedger = ShippedMigrations
            .Discover([new BarakoCMS.Forms.FormsModule(), new BarakoCMS.Files.FilesModule(), new BarakoCMS.Email.Resend.ResendEmailModule()])
            .Where(m => Version.Parse(m.Version) < new Version(4, 6, 0))
            .ToList();

        preLedger.Should().HaveCount(10);
        preLedger.Should().OnlyContain(m => m.SkipWhen != null);
        preLedger.Where(m => !m.Transactional).Select(m => m.Key).Should().Equal(
            new[] { "Files/4.2.0/stored-files-parent-index" },
            "that file builds its index CONCURRENTLY, and no other pre-ledger file does");
    }

    /// <summary>
    /// The gate for "a released migration is never edited". Released means the version folder is not
    /// above core's own version, which the release commit sets: so the release that ships a new
    /// folder fails here until its files are pinned, and from then on an edit to one fails here
    /// instead of at somebody's deploy.
    /// </summary>
    [Fact]
    public void A_released_migration_file_has_not_been_edited()
    {
        var core = Core.GetName().Version!;
        var releasedThrough = new Version(core.Major, core.Minor, core.Build);
        var shipped = FirstParty();
        var released = shipped.Where(m => Version.Parse(m.Version) <= releasedThrough).ToList();

        var problems = new List<string>();
        foreach (var migration in released)
        {
            if (!Released.TryGetValue(migration.Key, out var pinned))
            {
                problems.Add($"{migration.Key} is in a released version and is not pinned. Add to Released: "
                    + $"[\"{migration.Key}\"] = \"{migration.Checksum}\",");
            }
            else if (pinned != migration.Checksum)
            {
                problems.Add($"{migration.Key} was edited after it was released (pinned {pinned}, now {migration.Checksum}). "
                    + "Put the file back and add a new migration file for the change.");
            }
        }

        problems.AddRange(Released.Keys
            .Except(shipped.Select(m => m.Key))
            .Select(key => $"{key} is pinned as released and is no longer shipped. A released migration is not removed, renamed or moved to another owner."));

        releasedThrough.Should().BeGreaterThanOrEqualTo(new Version(4, 5, 0), "core's assembly version is what marks a folder as released");
        released.Count.Should().BeGreaterThanOrEqualTo(10, "ten files were released before the ledger");
        problems.Should().BeEmpty(
            "a released migration file is never edited, comments included: every database that ran it would refuse "
            + "to migrate until someone ran db-migrate --record by hand");
    }

    /// <summary>
    /// A database migrated by hand has no ledger, so its first run executes every file that has no
    /// skip query. A file added from 4.6.0 on therefore has to carry one, or say in so many words that
    /// a second run is harmless.
    /// </summary>
    [Fact]
    public void Every_file_added_since_the_ledger_says_what_happens_where_its_change_is_already_in_place()
    {
        var silent = FirstParty()
            .Where(m => Version.Parse(m.Version) >= new Version(4, 6, 0))
            .Where(m => !m.SafeWhereAlreadyApplied)
            .Select(m => m.Key)
            .ToList();

        silent.Should().BeEmpty(
            "each of these needs a `-- barako:skip-when: <query>` line, or `-- barako:rerunnable` if running it twice "
            + "changes nothing (docs/migrations.md, \"Writing a migration file\")");
    }

    // The control for the test above: without it, a rule that flags nothing passes on an empty list.
    [Fact]
    public void A_file_with_neither_a_skip_query_nor_a_rerunnable_line_is_the_one_that_rule_flags()
    {
        var bare = ShippedMigrations.Parse("core", "4.6.0/bare.sql", "update public.things set n = n + 1;");
        var vouched = ShippedMigrations.Parse("core", "4.6.0/vouched.sql", "-- barako:rerunnable\ncreate table if not exists public.things (n int);");
        var guarded = ShippedMigrations.Parse("core", "4.6.0/guarded.sql", "-- barako:skip-when: select true\nselect 1;");

        bare.SafeWhereAlreadyApplied.Should().BeFalse();
        bare.Rerunnable.Should().BeFalse();
        vouched.SafeWhereAlreadyApplied.Should().BeTrue();
        vouched.Rerunnable.Should().BeTrue();
        guarded.SafeWhereAlreadyApplied.Should().BeTrue();
    }

    [Fact]
    public void Two_enabled_modules_cannot_both_own_one_assemblys_migrations()
    {
        IBarakoModule[] modules = [new BarakoCMS.Files.FilesModule(), new SecondOwnerOfTheFilesAssembly()];

        var act = () => ShippedMigrations.Discover(modules);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Files").And.Contain("Second Owner");
    }

    [Fact]
    public void The_checksum_ignores_line_endings_and_nothing_else()
    {
        var unix = ShippedMigrations.Parse("core", "4.6.0/a.sql", "select 1;\nselect 2;\n");
        var windows = ShippedMigrations.Parse("core", "4.6.0/a.sql", "select 1;\r\nselect 2;\r\n");
        var edited = ShippedMigrations.Parse("core", "4.6.0/a.sql", "select 1;\nselect 3;\n");

        windows.Checksum.Should().Be(unix.Checksum, "a checkout with CRLF is the same file");
        edited.Checksum.Should().NotBe(unix.Checksum);
        unix.Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_file_states_its_skip_query_and_whether_it_needs_a_transaction()
    {
        var plain = ShippedMigrations.Parse("core", "4.6.0/plain.sql", "-- an ordinary comment\nselect 1;");
        var marked = ShippedMigrations.Parse("Files", "4.6.0\\marked.sql",
            "-- barako:skip-when: select to_regclass('public.x') is not null\n-- barako:no-transaction\nselect 1;");

        plain.SkipWhen.Should().BeNull();
        plain.Transactional.Should().BeTrue();
        marked.SkipWhen.Should().Be("select to_regclass('public.x') is not null");
        marked.Transactional.Should().BeFalse();
        marked.Key.Should().Be("Files/4.6.0/marked", "a resource built on Windows carries a backslash");
    }

    [Theory]
    [InlineData("core", "4.6.0/a.sql", "-- barako:skip-wen: select true\nselect 1;", "does not know")]
    [InlineData("core", "4.6.0/a.sql", "-- barako:skip-when: select true\n-- barako:skip-when: select false\nselect 1;", "more than one")]
    [InlineData("core", "4.6.0/a.sql", "-- barako:skip-when:\nselect 1;", "empty")]
    [InlineData("core", "4.6.0/a.sql", "-- barako:no-transaction\ncreate index concurrently i on t (c);", "neither a skip-when query nor")]
    [InlineData("core", "tenancy/001-app-role.sql", "select 1;", "is not named")]
    [InlineData("core", "4.6/a.sql", "select 1;", "is not named")]
    [InlineData("core", "4.6.0/nested/a.sql", "select 1;", "is not named")]
    [InlineData("a/b", "4.6.0/a.sql", "select 1;", "no slash")]
    public void A_file_this_build_cannot_place_is_refused_not_guessed_at(string owner, string path, string sql, string expected)
    {
        var act = () => ShippedMigrations.Parse(owner, path, sql);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(expected);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
