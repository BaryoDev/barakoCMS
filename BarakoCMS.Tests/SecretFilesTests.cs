using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Secrets handed over as files (Docker or Kubernetes secrets) become configuration, with the same
/// guards applying as for an environment value. Every fixture value is a made-up one.
/// </summary>
public sealed class SecretFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "barako-secrets-" + Guid.NewGuid().ToString("N"));

    public SecretFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    private static IConfiguration Build(Action<IConfigurationBuilder> add)
    {
        var builder = new ConfigurationBuilder();
        add(builder);
        return builder.Build();
    }

    [Fact]
    public void A_file_named_with_double_underscores_becomes_a_sectioned_key()
    {
        Write("JWT__Key", "fake-file-key-0123456789-0123456789");

        var config = Build(b => b.AddSecretFiles(_dir));

        config["JWT:Key"].Should().Be("fake-file-key-0123456789-0123456789");
    }

    [Fact]
    public void A_secret_file_wins_over_an_environment_value_for_the_same_key()
    {
        Write("JWT__Key", "from-the-file");

        var config = Build(b => b
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:Key"] = "from-the-environment" })
            .AddSecretFiles(_dir));

        config["JWT:Key"].Should().Be("from-the-file");
    }

    [Theory]
    [InlineData("value\n")]
    [InlineData("value\r\n")]
    [InlineData("value\n\n\r\n")]
    public void Trailing_newlines_are_not_part_of_the_value(string content)
    {
        Write("JWT__Key", content);

        var config = Build(b => b.AddSecretFiles(_dir));

        config["JWT:Key"].Should().Be("value");
    }

    [Fact]
    public void Only_trailing_newlines_are_removed()
    {
        Write("A__B", "  spaced value \t\nsecond line\n");

        var config = Build(b => b.AddSecretFiles(_dir));

        config["A:B"].Should().Be("  spaced value \t\nsecond line");
    }

    [Fact]
    public void A_missing_directory_adds_nothing_and_does_not_fail()
    {
        var config = Build(b => b.AddSecretFiles(Path.Combine(_dir, "does-not-exist")));

        config.AsEnumerable().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_directory_setting_means_the_default_directory(string? setting)
    {
        Write("JWT__Key", "from-the-default");

        var config = Build(b => b.AddSecretFilesFromEnvironment(
            name => name == SecretFiles.DirectorySetting ? setting : null,
            defaultDirectory: _dir));

        config["JWT:Key"].Should().Be("from-the-default");
    }

    [Fact]
    public void The_directory_setting_names_the_directory()
    {
        Write("JWT__Key", "from-the-setting");

        var config = Build(b => b.AddSecretFilesFromEnvironment(
            name => name == SecretFiles.DirectorySetting ? _dir : null,
            defaultDirectory: Path.Combine(_dir, "does-not-exist")));

        config["JWT:Key"].Should().Be("from-the-setting");
    }

    [Fact]
    public void An_unreadable_file_stops_startup_naming_the_file_and_not_its_content()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "chmod does not restrict read access on Windows");
        const string content = "fake-content-that-must-not-leak";
        Write("JWT__Key", content);
        var path = Path.Combine(_dir, "JWT__Key");
        File.SetUnixFileMode(path, UnixFileMode.None);
        Assert.SkipWhen(CanRead(path), "running as a user that ignores file modes");

        var act = () => Build(b => b.AddSecretFiles(_dir));

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("JWT__Key");
        message.Should().NotContain(content);
    }

    [Fact]
    public void A_directory_that_cannot_be_listed_stops_startup_naming_the_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "chmod does not restrict directory access on Windows");
        var locked = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanList(locked), "running as a user that ignores directory modes");

            var act = () => Build(b => b.AddSecretFiles(locked));

            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain(locked).And.Contain("1654");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool CanList(string path)
    {
        try { _ = Directory.EnumerateFiles(path).ToArray(); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool CanRead(string path)
    {
        try { using var _ = File.OpenRead(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void The_guards_apply_to_a_jwt_key_read_from_a_file()
    {
        // An example key this repository prints, so a deployment that copied it into a secret file is refused.
        Write("JWT__Key", "change-me-to-at-least-32-characters-secret\n");
        var config = Build(b => b.AddSecretFiles(_dir));

        var act = () => JwtKeyGuard.Validate(config["JWT:Key"], isDevelopment: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*example*");
    }

    [Fact]
    public void The_guards_apply_to_a_database_password_read_from_a_file()
    {
        Write("ConnectionStrings__DefaultConnection", "Host=postgres;Database=barako_cms;Username=postgres;Password=change-me-to-a-strong-password\n");
        var config = Build(b => b.AddSecretFiles(_dir));

        var act = () => DatabasePasswordGuard.Validate(
            config.GetConnectionString("DefaultConnection"), isDevelopment: false, environment: _ => null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*database password*refused*");
    }

    [Fact]
    public void A_strong_value_in_a_file_passes_the_guards()
    {
        Write("JWT__Key", "fake-but-unremarkable-key-0123456789abcdef\n");
        Write("ConnectionStrings__DefaultConnection", "Host=postgres;Database=barako_cms;Username=postgres;Password=fake-unremarkable-0123456789\n");
        var config = Build(b => b.AddSecretFiles(_dir));

        JwtKeyGuard.Validate(config["JWT:Key"], isDevelopment: false).Should().NotBeNullOrEmpty();
        var db = () => DatabasePasswordGuard.Validate(
            config.GetConnectionString("DefaultConnection"), isDevelopment: false, environment: _ => null);
        db.Should().NotThrow();
    }
}

/// <summary>
/// The shipped Caddyfile and the secrets override, read as text like <see cref="ComposeDefaultsTests"/>.
/// </summary>
public class SecretFilesShippedFilesTests
{
    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(RepoPaths.Root, file.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("Server")]
    [InlineData("Via")]
    public void The_Caddyfile_removes_the_header_that_names_the_proxy(string header)
    {
        var active = string.Join('\n', Read("Caddyfile").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        Regex.IsMatch(active, $@"^\s*-{header}\s*$|^\s*header\s+-{header}\s*$", RegexOptions.Multiline)
            .Should().BeTrue("Caddy sends {0} on every response unless the site block removes it", header);
    }

    /// <summary>The target names the override gives the app's secrets, with the configuration key each one is.</summary>
    private static List<string> AppTargets()
    {
        var text = Read("docker-compose.secrets.yml");
        var app = Regex.Match(text, @"^  app:\n(?<body>(?:    .*\n|\n)+)", RegexOptions.Multiline).Groups["body"].Value;
        return Regex.Matches(app, @"target:\s*(?<t>\S+)").Select(m => m.Groups["t"].Value).ToList();
    }

    [Fact]
    public void The_secrets_override_mounts_each_app_secret_under_a_configuration_key_the_app_reads()
    {
        var targets = AppTargets();
        targets.Should().BeEquivalentTo(new[] { "JWT__Key", "InitialAdmin__Password", "ConnectionStrings__DefaultConnection" });

        var sources = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "barakoCMS"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var target in targets)
        {
            var key = target.Replace("__", ":");
            var section = key.Split(':');
            var read = section[0] == "ConnectionStrings"
                ? $"GetConnectionString(\"{section[1]}\")"
                : $"\"{key}\"";
            sources.Any(s => s.Contains(read)).Should().BeTrue("{0} must be a key the app reads ({1})", target, read);
        }
    }

    [Fact]
    public void The_secrets_override_gives_postgres_a_password_file_and_blanks_the_plain_value()
    {
        var text = Read("docker-compose.secrets.yml");

        text.Should().Contain("POSTGRES_PASSWORD_FILE: /run/secrets/db_password");
        text.Should().Contain("POSTGRES_PASSWORD: \"\"");
    }

    [Fact]
    public void The_prod_file_asks_for_a_secret_by_variable_or_by_the_override_switch_and_never_by_neither()
    {
        var lines = Read("docker-compose.prod.yml").Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToList();

        foreach (var variable in new[] { "JWT_KEY", "ADMIN_PASSWORD", "DB_PASSWORD" })
        {
            lines.Should().Contain(l => l.Contains("${" + variable + ":-${SECRETS_AS_FILES:?"),
                "{0} stays required, and only SECRETS_AS_FILES=true lets a secret file stand in", variable);
        }
    }

    [Fact]
    public void The_switch_appears_only_in_the_apps_three_secret_lines_and_every_other_password_is_the_plain_variable()
    {
        var lines = Read("docker-compose.prod.yml").Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToList();

        var withSwitch = lines.Where(l => l.Contains("SECRETS_AS_FILES")).ToList();
        withSwitch.Should().HaveCount(3);
        withSwitch.Should().OnlyContain(l => l.Contains("JWT__Key=") || l.Contains("InitialAdmin__Password=")
                                          || l.Contains("ConnectionStrings__DefaultConnection="),
            "a database password that can resolve to the switch's own value initialises Postgres with it");

        var passwords = lines.Where(l => l.StartsWith("POSTGRES_PASSWORD:") || l.StartsWith("PGPASSWORD:")).ToList();
        passwords.Should().HaveCount(3, "postgres, postgres-extensions and db-backup");
        passwords.Should().OnlyContain(l => l.EndsWith("${DB_PASSWORD:-}"));
    }

    [Fact]
    public void Program_adds_the_secret_files_once_after_the_builder_and_before_configuration_is_read()
    {
        var code = string.Join('\n', Read("barakoCMS/Program.cs").Split('\n').Select(l =>
        {
            var i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));

        const string call = "AddSecretFilesFromEnvironment";
        Regex.Matches(code, call).Should().HaveCount(1);
        var at = code.IndexOf(call, StringComparison.Ordinal);

        var created = code.IndexOf("WebApplication.CreateBuilder", StringComparison.Ordinal);
        created.Should().BeGreaterThanOrEqualTo(0);
        at.Should().BeGreaterThan(created, "the builder has to exist first");

        foreach (var read in new[] { "ReadFrom.Configuration", "builder.Configuration[", "GetSection", "Get<", "GetValue" })
        {
            var first = code.IndexOf(read, StringComparison.Ordinal);
            if (first >= 0)
            {
                at.Should().BeLessThan(first, "a secret file has to be in configuration before {0} reads it", read);
            }
        }
    }

    [Fact]
    public void The_secrets_folder_is_ignored_by_git()
    {
        Read(".gitignore").Split('\n').Select(l => l.Trim()).Should().Contain("/secrets/");
    }
}
