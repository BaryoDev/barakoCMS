using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What the owner (a superuser) session runs, and what a restore runs as, read from the files
/// that say so: the migrations, the scripts and the docs. Each failure names the file.
/// </summary>
public sealed partial class OwnerSessionStaticTests
{
    private const string Pin = "PGOPTIONS='-c search_path=pg_catalog,pg_temp'";

    private const string RoleJob = "run --rm postgres-extensions";

    /// <summary>The awk program that drops extension lines from a restore stream and leaves COPY data alone.</summary>
    private const string ExtensionFilter =
        """c { print; if ($0 == "\\.") c = 0; next } /^COPY .* FROM stdin;$/ { c = 1 } /^(CREATE|COMMENT ON) EXTENSION / { next } { print }""";

    /// <summary>
    /// The migrations that run as a superuser or an owner. The list is closed: from 6.0 no new
    /// migration needs one, so a file that matches <see cref="NeedsOwner"/> and is not here fails.
    /// </summary>
    private static readonly string[] OwnerFiles =
    [
        "4.6.0/sensitivity-by-capability.sql",
        "4.6.0/rollback-sensitivity-by-capability.sql",
        "4.6.0/tenant-profile-to-site.sql",
        "4.6.0/rollback-tenant-profile-to-site.sql",
        "5.0.0/role-tenancy.sql",
        "5.0.0/rollback-role-tenancy.sql",
        "5.0.0/site-field-schemas.sql",
        "5.0.0/rollback-site-field-schemas.sql",
        "5.6.1/site-fonts-schema.sql",
        "5.6.1/rollback-site-fonts-schema.sql",
    ];

    /// <summary>
    /// Files that match <see cref="NeedsOwner"/> because their header allows a superuser, and that run as
    /// the app role, the role that owns the tables. The header of a released file cannot be edited (its
    /// checksum is pinned), so the file stays in the match and out of the owner list.
    /// </summary>
    private static readonly string[] AppRoleFiles = ["4.7.0/tenant-policy-restore.sql"];

    /// <summary>The line in Database roles that says when the owner files run.</summary>
    private const string OrderRule =
        "they run before the `postgres-extensions` job, and once the job has run they are not run as the owner";

    /// <summary>The same words scripts/lib-migrations.sh migration_needs_owner looks for.</summary>
    [GeneratedRegex("rolsuper|rolbypassrls|as a superuser|or a superuser", RegexOptions.IgnoreCase)]
    private static partial Regex NeedsOwner();

    [GeneratedRegex("rolsuper|rolbypassrls", RegexOptions.IgnoreCase)]
    private static partial Regex ChecksForSuperuser();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoPaths.Root, relative));

    private static IEnumerable<(string Path, string Text)> Migrations() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "migrations"), "*.sql", SearchOption.AllDirectories)
            .Select(file => (Path.GetRelativePath(Path.Combine(RepoPaths.Root, "migrations"), file).Replace('\\', '/'), File.ReadAllText(file)));

    private static string Section(string file, string heading)
    {
        var text = Read(file);
        var start = text.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "{0} has a section called \"{1}\"", file, heading);
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    [Fact]
    public void Every_file_that_needs_a_superuser_is_in_the_released_list_and_no_new_one_is_added()
    {
        var all = Migrations().Where(m => !m.Path.StartsWith("removed/", StringComparison.Ordinal)).ToList();
        all.Should().HaveCountGreaterThan(OwnerFiles.Length, "the scan sees the shipped migrations");

        var found = all.Where(m => NeedsOwner().IsMatch(m.Text)).Select(m => m.Path).Order(StringComparer.Ordinal).ToList();
        var expected = OwnerFiles.Concat(AppRoleFiles).Order(StringComparer.Ordinal).ToList();

        found.Except(expected).Should().BeEmpty(
            "from 6.0 no new migration needs a superuser (it runs as the app role and lifts FORCE ROW LEVEL SECURITY in its own transaction, see docs/migrations.md), "
            + "so a new file must not ask for one");
        found.Should().Equal(expected,
            "a file in the owner list that no longer asks for a superuser has to leave the list, and the docs, with it");

        var offenders = all
            .Where(m => !OwnerFiles.Contains(m.Path) && ChecksForSuperuser().IsMatch(m.Text))
            .Select(m => "migrations/" + m.Path)
            .ToList();
        offenders.Should().BeEmpty(
            "{0} mentions rolsuper or rolbypassrls, and from 6.0 a new migration does not need a superuser. See docs/migrations.md",
            string.Join(", ", offenders));
    }

    [Fact]
    public void Every_rollback_file_in_the_owner_list_pins_the_search_path_and_puts_it_back()
    {
        var rollbacks = OwnerFiles.Where(f => Path.GetFileName(f).StartsWith("rollback-", StringComparison.Ordinal)).ToList();
        rollbacks.Should().HaveCount(5, "five rollback files are on the owner list");

        var checkedFiles = 0;
        foreach (var file in rollbacks)
        {
            var lines = Read("migrations/" + file).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToList();
            lines.First().Should().Be("SET search_path = pg_catalog, pg_temp;",
                "migrations/{0} runs as a superuser and its first statement sets the search path to pg_catalog, pg_temp", file);
            lines.Last().Should().Be("RESET search_path;", "migrations/{0} puts the search path back at the end", file);
            checkedFiles++;
        }
        checkedFiles.Should().Be(rollbacks.Count);
    }

    [Fact]
    public void The_docs_give_the_pinned_command_for_every_owner_file_and_the_order_rule()
    {
        var roles = Section("docs/deploy-in-production.md", "Database roles");
        roles.Should().Contain(Pin, "the Database roles section gives the command that runs an owner file with the search path pinned");
        roles.Should().Contain("-c barako.only_role=<id>'", "the Database roles section gives the combined form for a file that sets a scope");
        var listed = 0;
        foreach (var file in OwnerFiles)
        {
            roles.Should().Contain("migrations/" + file, "the Database roles section lists every file that runs as the owner, and migrations/{0} is missing", file);
            listed++;
        }
        listed.Should().Be(OwnerFiles.Length);
        foreach (var file in AppRoleFiles)
            roles.Should().Contain("migrations/" + file + "` is not on that list",
                "the Database roles section says migrations/{0} runs as the app role", file);
        Regex.Replace(roles, @"\s+", " ").Should().Contain(OrderRule, "the Database roles section has the one rule on when the owner files run");

        var check = Read("scripts/upgrade-check.sh");
        check.Should().Contain("migration_needs_owner", "scripts/upgrade-check.sh picks the owner files with migration_needs_owner");
        check.Should().Contain(
            "docker exec ${pin[@]+\"${pin[@]}\"} \"$PG\" psql -U postgres -d barako_cms -v ON_ERROR_STOP=1 ${single[@]+\"${single[@]}\"} -f /tmp/migration.sql >/dev/null",
            "scripts/upgrade-check.sh applies each file with the pin in front when it is an owner file");
        check.Should().Contain("pin=(-e PGOPTIONS='-c search_path=pg_catalog,pg_temp')");
        Read("scripts/lib-migrations.sh").Should().Contain("migration_needs_owner()");
        Read("scripts/lib-migrations.sh").Should().Contain("PGOPTIONS='-c search_path=pg_catalog,pg_temp' ", "the plan prints the pin in front of an owner file");
    }

    [Fact]
    public void No_owner_migration_step_in_the_upgrade_docs_comes_after_the_role_job()
    {
        var upgrading = Section("docs/deploy-in-production.md", "Upgrading");
        var job = upgrading.IndexOf(RoleJob, StringComparison.Ordinal);
        job.Should().BeGreaterThanOrEqualTo(0, "the Upgrading section runs the role job ({0})", RoleJob);

        var markers = OwnerFiles.Select(f => "migrations/" + f).Append(Pin).ToList();
        var examined = 0;
        foreach (var marker in markers)
        {
            var at = upgrading.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
                at.Should().BeLessThan(job,
                    "docs/deploy-in-production.md Upgrading has the owner step {0} after the role job, and an owner migration runs before it, while nothing in public belongs to the app role yet", marker);
            examined++;
        }
        examined.Should().Be(markers.Count);
    }

    [Fact]
    public void Restores_run_as_the_app_role_after_the_owner_job_and_drop_the_extension_lines()
    {
        var restoreDb = Read("scripts/restore-db.sh");
        restoreDb.Should().Contain("config --services", "scripts/restore-db.sh asks compose whether the stack has the owner job");
        restoreDb.Should().Contain("postgres-extensions");
        restoreDb.Should().Contain("PGOPTIONS=\"-c role=", "scripts/restore-db.sh loads the dump as the app role");
        restoreDb.Should().Contain(ExtensionFilter, "scripts/restore-db.sh drops the extension lines with the shared filter");
        restoreDb.Should().Contain("runs as the database owner", "on a stack without the job scripts/restore-db.sh says it restores as the owner");
        restoreDb.Should().Contain("Nothing was changed", "scripts/restore-db.sh stops when compose config fails, before it changes anything");
        restoreDb.Should().Contain("run --rm --no-deps -T postgres-extensions", "the owner job runs without a tty");
        Regex.Count(restoreDb, @"^\s*read -p", RegexOptions.Multiline).Should().Be(1, "scripts/restore-db.sh asks once, after it knows which path it takes");
        var bare = restoreDb.Split('\n').Where(l => Regex.IsMatch(l, @"^[^#]*\bdocker compose\b") && !l.TrimStart().StartsWith("echo", StringComparison.Ordinal) && !l.Contains("DC=(docker compose)")).ToList();
        bare.Should().BeEmpty("every compose call in scripts/restore-db.sh goes through the one chosen file: {0}", string.Join(" | ", bare));

        var restoreCheck = Read("scripts/restore-check.sh");
        restoreCheck.Should().Contain("-c role=$APP_ROLE", "scripts/restore-check.sh restores as the app role");
        restoreCheck.Should().Contain(ExtensionFilter);
        restoreCheck.Should().Contain("does not exist|permission denied", "scripts/restore-check.sh fails on a missing object or a permission error in the restore output");
        restoreCheck.Should().Contain("deptype = 'e'", "scripts/restore-check.sh leaves extension members out of the ownership check");
        restoreCheck.Should().Contain("pg_policies").And.Contain("relforcerowsecurity");
        restoreCheck.Should().Contain("Tenancy__DatabaseEnforcement=true");
        restoreCheck.Should().NotContain("AllowSuperuser", "the API in scripts/restore-check.sh connects as the app role");

        var offsite = Read("scripts/offsite-backup-check.sh");
        offsite.Should().Contain("-c role=$APP_ROLE", "scripts/offsite-backup-check.sh restores as a role that is not a superuser");
        var awk = Regex.Match(offsite, @"\| awk '(?<f>[^\n]*)' \\\n");
        awk.Success.Should().BeTrue("scripts/offsite-backup-check.sh pipes the restore through awk");
        awk.Groups["f"].Value.Replace(@"\\\\", @"\\").Replace(@"\$", "$").Replace("\\\"", "\"")
            .Should().Be(ExtensionFilter, "scripts/offsite-backup-check.sh has the shared filter, escaped for its sh -c string");

        var docs = Read("docs/backup-and-restore.md");
        Regex.Count(docs, Regex.Escape(ExtensionFilter)).Should().BeGreaterThanOrEqualTo(2,
            "docs/backup-and-restore.md has the filter in the production and the quickstart block");
        Regex.Count(docs, Regex.Escape("-c role=${DB_APP_USER:-barako_app}")).Should().BeGreaterThanOrEqualTo(2);
        Regex.Count(docs, Regex.Escape("run --rm --no-deps postgres-extensions")).Should().BeGreaterThanOrEqualTo(2);

        foreach (var file in new[] { "scripts/restore-db.sh", "scripts/restore-check.sh", "scripts/offsite-backup-check.sh", "docs/backup-and-restore.md", "scripts/backup-cron.sh" })
            Read(file).Should().NotContain("--no-comments", "{0} keeps comments, which Marten may read", file);
    }

    [Fact]
    public void The_backup_script_comment_says_restores_run_as_the_app_role_in_both_copies()
    {
        var core = Read("scripts/backup-cron.sh");
        core.Should().Contain("restores run as the app role", "scripts/backup-cron.sh says how a restore runs");
        Read("quickstart/scripts/backup-cron.sh").Should().Be(core, "quickstart/scripts/backup-cron.sh is a copy of scripts/backup-cron.sh");
    }
}
