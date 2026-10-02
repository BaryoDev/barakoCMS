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
