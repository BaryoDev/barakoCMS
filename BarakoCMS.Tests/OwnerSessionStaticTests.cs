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
        "4.7.0/tenant-policy-restore.sql",
        "5.0.0/role-tenancy.sql",
        "5.0.0/rollback-role-tenancy.sql",
        "5.0.0/site-field-schemas.sql",
        "5.0.0/rollback-site-field-schemas.sql",
        "5.6.1/site-fonts-schema.sql",
        "5.6.1/rollback-site-fonts-schema.sql",
    ];

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
        var found = Migrations()
            .Where(m => !m.Path.StartsWith("removed/", StringComparison.Ordinal) && NeedsOwner().IsMatch(m.Text))
            .Select(m => m.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        var unlisted = found.Except(OwnerFiles).ToList();
        unlisted.Should().BeEmpty(
            "from 6.0 no new migration needs a superuser (it runs as the app role and lifts FORCE ROW LEVEL SECURITY in its own transaction, see docs/migrations.md), "
            + "so migrations/{0} must not ask for one", string.Join(", migrations/", unlisted));
        found.Should().Equal(OwnerFiles.Order(StringComparer.Ordinal),
            "a file in the owner list that no longer asks for a superuser has to leave the list, and the docs, with it");
    }

    [Fact]
    public void No_file_outside_the_released_list_checks_for_a_superuser()
    {
        var offenders = Migrations()
            .Where(m => !OwnerFiles.Contains(m.Path) && ChecksForSuperuser().IsMatch(m.Text))
            .Select(m => "migrations/" + m.Path)
            .ToList();
        offenders.Should().BeEmpty(
            "{0} mentions rolsuper or rolbypassrls, and from 6.0 a new migration does not need a superuser. See docs/migrations.md",
            string.Join(", ", offenders));
    }

    [Fact]
    public void The_rollback_files_that_check_for_a_superuser_pin_the_search_path_before_their_first_statement()
    {
        foreach (var file in OwnerFiles.Where(f => f.Contains("rollback-", StringComparison.Ordinal)))
        {
            var text = Read("migrations/" + file);
            if (!ChecksForSuperuser().IsMatch(text)) continue;

            var first = text.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal));
            first.Should().Be("SET search_path = pg_catalog, pg_temp;",
                "migrations/{0} runs as a superuser and its first statement sets the search path to pg_catalog, pg_temp", file);
        }
    }

    [Fact]
    public void The_docs_give_the_pinned_command_for_every_owner_file()
    {
        var roles = Section("docs/deploy-in-production.md", "Database roles");
        roles.Should().Contain(Pin, "the Database roles section gives the command that runs an owner file with the search path pinned");
        foreach (var file in OwnerFiles)
            roles.Should().Contain("migrations/" + file, "the Database roles section lists every file that runs as the owner, and migrations/{0} is missing", file);

        var check = Read("scripts/upgrade-check.sh");
        check.Should().Contain("PGOPTIONS='-c search_path=pg_catalog,pg_temp'", "scripts/upgrade-check.sh runs the owner files with the pin");
        check.Should().Contain("migration_needs_owner", "scripts/upgrade-check.sh picks the owner files with migration_needs_owner");
        Read("scripts/lib-migrations.sh").Should().Contain("migration_needs_owner()");
    }

    [Fact]
    public void An_owner_migration_step_in_the_upgrade_docs_comes_before_the_role_job()
    {
        var upgrading = Section("docs/deploy-in-production.md", "Upgrading");
        var job = upgrading.IndexOf(RoleJob, StringComparison.Ordinal);
        job.Should().BeGreaterThanOrEqualTo(0, "the Upgrading section runs the role job ({0})", RoleJob);

        var markers = OwnerFiles.Select(f => "migrations/" + f).Append(Pin);
        foreach (var marker in markers)
        {
            var at = upgrading.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            at.Should().BeLessThan(job,
                "docs/deploy-in-production.md Upgrading has the owner step {0} after the role job, and an owner migration runs before it, while nothing in public belongs to the app role yet", marker);
        }
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
        offsite.Should().Contain("(CREATE|COMMENT ON) EXTENSION");

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
