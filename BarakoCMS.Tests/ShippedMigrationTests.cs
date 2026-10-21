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
        "4.6.0/external-auth-identities",
        "4.6.0/forms-email-verification",
        "4.7.0/forms-tenant-policy-restore",
        "4.8.0/external-auth-used-nonces",
        "5.0.0/accounting-number-sequences",
        "5.0.0/pages-settings",
        "5.0.0/files-storage-settings",
        "5.1.0/915-external-auth-role-maps",
        "5.2.0/booking-resources",
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
        ["core/4.6.0/event-correlation-metadata"] = "0a14352be55a4384536624ffcf789daa8044096ffd5de88193b0f884a4e3530e",
        ["core/4.6.0/sensitivity-by-capability"] = "6d3a18f101477fcd92164695340f1e3f243ab6e00a0a8afbd2aa6b275af7b068",
        ["core/4.6.0/tenant-profile-to-site"] = "5dbaa5114defc5fd920d4fd05864bd454cb72080b75019f950a1ad6f53e1e277",
        ["ExternalAuth/4.6.0/external-auth-identities"] = "b8d8179181f7263374edc337f97e00b92fcd988f25524f148d90c55efda6dc08",
        ["Forms/4.6.0/forms-email-verification"] = "44e209551d9b71301fd29ef8e99cb56f64842cc18f31c724048399924bf1c4c0",
        ["core/4.7.0/membership-unique-user-tenant"] = "80f9d4e5fab5ab7b8053c47634fde7a3caabf6ed594f466949b4d2bb49e86303",
        ["core/4.7.0/tenant-policy-restore"] = "79195b3f5af298a2dc7fd457462f220da290046fdd15cb1dcc0988014d8d0758",
        ["Forms/4.7.0/forms-tenant-policy-restore"] = "fed503d62bd158382f74db7dea413a15f1ccb4ee3df25a9efec469a450e0dae3",
        ["ExternalAuth/4.8.0/external-auth-used-nonces"] = "3c5a28d80e303db280fee5124aa24073666476fd22fd5254fae1c3d3b20c7f5f",
        ["core/4.9.0/gapless-numbering"] = "546a734ce315e86e89aa05de8ad598f40825343001a73a958f6265bdbab88b5d",
        ["core/4.9.0/stations"] = "24c899bb202fbcef6316181dc965947e243cafd89a5d4b16ee9aa704f9dbabe5",
        ["Accounting/5.0.0/accounting-number-sequences"] = "1b0c692e36b40fdebbe07a951be5c405e981d2eac6bfc7d58eed09d75194cd80",
        ["core/5.0.0/blocks"] = "157480041989f0e2c94ecdabaab38be675f36090ba2f997a17581d3e7a624d96",
        ["core/5.0.0/field-changes"] = "ea34638b75ca37c5cf1e6363a07e685e9feb86e6c43d6f439c740af11efbbf0b",
        ["Files/5.0.0/files-storage-settings"] = "cdad1c10b7f0e90cc1a9c91b513260d78413b2abc25b03f05741e22aa37be7fd",
        ["core/5.0.0/marten-9-49-quick-append-events"] = "c7d141e0a1f987e0c7a44def68e64f51ab6f4b443e76f1d6b0826fbba1e72c04",
        ["Pages/5.0.0/pages-settings"] = "204e781e76d6adae347497950771293b05a55d65e19f9cd65743e0caf90a6181",
        ["core/5.0.0/role-tenancy"] = "1cdf8bea3d8efbd67134cfc02b154042e620cabe469b5b525c0059636c70be17",
        ["core/5.0.0/site-field-schemas"] = "982871901171d8bf8bae595987abe0d560b1e71771f6e0d57e721bdced3ee4ad",
        ["core/5.0.0/tenant-status"] = "158094c6e3234029157f411909efde433353c5ae00c73481ecb78f6ed67c8d52",
        ["core/5.1.0/1158-content-type-name-index-ignore-case"] = "2f131a9e9d0a65db5255ed7221a4e3896052639f4413e553f7c6c19450e22040",
        ["ExternalAuth/5.1.0/915-external-auth-role-maps"] = "8df8e9bb5661a91bcf896ee15dbe87b560bfa90d705d8d51cbf4967ff02b3507",
        ["core/5.1.0/seed-markers"] = "0635c09690d8d33fe92ef8189277aff93383d708ff9a28df5a75f4dbd65d421e",
        ["core/5.1.0/workflow-run-updated-at-index"] = "096c752f26b5372a0fc3b8858620742a80482d241f0242a0a5e1090f9c6dab0a",
        ["core/5.1.0/workflow-run-workflow-index"] = "588062405fa26596a8472b717f804c18d29e6495c2a7d570b9533a825f2e7024",
        ["core/5.2.0/910-inbound-webhook-events"] = "96514e93595f69e06588ee266cb1d0376be8661898910a4bc27aef855225f710",
        ["core/5.2.0/action-link-uses"] = "5cb1eeda12dcc8fc801327f845e531c4c62285eae3ef3dcd6e50f89956341c5d",
        ["core/5.2.0/magic-link-tokens"] = "ab39ed40193ba3fed9de40089ee02496f012debaf11b9cad91abd9e4c7c03909",
        ["core/5.2.0/outbox-messages"] = "e19d42500768b05e6406ff2f4c614431f24b4790e95d217008532a0ec1dc6ee1",
        ["core/5.2.0/pending-builds"] = "a169671e0291d7153f383041f5d9faded2817d7bfbe7aad3fad5a7ba389763f2",
        ["core/5.2.0/signed-link-uses"] = "038b3f05443ea16918589b1bad1d11bc89311d406001e44794ade130c8fac426",
        ["core/5.2.0/workflow-schedule-claims"] = "51d6a157ef250942f01f3d4d5060fbdee32e9efee187f7551d7a42721e4f4371",
    };

    private static IReadOnlyList<ShippedMigration> FirstParty() =>
        ShippedMigrations.Discover(
        [
            new BarakoCMS.Forms.FormsModule(),
            new BarakoCMS.Files.FilesModule(),
            new BarakoCMS.Email.Resend.ResendEmailModule(),
            new BarakoCMS.ExternalAuth.ExternalAuthModule(),
            new BarakoCMS.Accounting.AccountingModule(),
            new BarakoCMS.Pages.PagesModule(),
        ]);

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
            new BarakoCMS.ExternalAuth.ExternalAuthModule(),
            new BarakoCMS.Accounting.AccountingModule(),
            new BarakoCMS.Pages.PagesModule(),
            new BarakoCMS.Booking.BookingModule(),
        ];

        var all = ShippedMigrations.Discover(modules);
        var coreOnly = ShippedMigrations.Discover([]);

        var owned = all.Where(m => m.Owner != ShippedMigrations.CoreOwner).Select(m => m.Key).ToList();
        owned.Should().Equal(
            "Accounting/5.0.0/accounting-number-sequences",
            "Booking/5.2.0/booking-resources",
            "Email.Resend/4.5.0/email-sent-emails",
            "ExternalAuth/4.6.0/external-auth-identities",
            "ExternalAuth/4.8.0/external-auth-used-nonces",
            "ExternalAuth/5.1.0/915-external-auth-role-maps",
            "Files/4.2.0/stored-files-parent-index",
            "Files/5.0.0/files-storage-settings",
            "Forms/4.2.0/forms-public-forms",
            "Forms/4.6.0/forms-email-verification",
            "Forms/4.7.0/forms-tenant-policy-restore",
            "Pages/5.0.0/pages-settings");
        coreOnly.Count.Should().BeGreaterThanOrEqualTo(7, "seven core files were released before the ledger");
        coreOnly.Should().HaveCount(all.Count - 12, "a module that is not enabled contributes nothing");
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
